// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed partial class LocalRewriter
    {

        public override BoundNode? VisitSqlStatement(BoundSqlStatement node)
        {
            var sqlTextBoundLiteral = _factory.Literal(node.SqlContents);

            BoundBlock? sqlDoBoundLiteral = null;
            ImmutableArray<LocalSymbol>? sqlDoSymbols = null;
            if (node.SqlDoOpt is not null)
            {
                sqlDoBoundLiteral = (BoundBlock)VisitBlock(node.SqlDoOpt.Body);
                sqlDoSymbols = node.SqlDoOpt.Locals;
            }

            return RewriteSqlStatement(
                node,
                sqlTextBoundLiteral,
                sqlDoBoundLiteral,
                null,
                null,
                sqlDoSymbols);
        }

        private BoundStatement RewriteSqlStatement(
            BoundNode node,
            BoundLiteral sqlTextBoundLiteral,
            BoundBlock? sqlDoBlock,
            BoundBlock? sqlEmptyBlock,
            BoundBlock? sqlEndBlock,
            ImmutableArray<LocalSymbol>? sqlDoSymbols)
        {
            var syntax = node.Syntax;

            // Types
            var iDataRecordType = _compilation.GetTypeByMetadataName("System.Data.IDataRecord");
            Debug.Assert(iDataRecordType is not null, "System.Data.IDataRecord not found");
            var sqlDataReaderType = _compilation.GetTypeByMetadataName("Microsoft.Data.SqlClient.SqlDataReader");
            Debug.Assert(sqlDataReaderType is not null, "Microsoft.Data.SqlClient.SqlDataReader not found");

            // Functions
            var connectMethodSymbol = TryLookupFunction(
                syntax,
                "iWare.Database.SqlCommands2",
                "Connect");
            Debug.Assert(connectMethodSymbol is not null, "iWare.Database.SqlCommands2.Connect not found");
            var disconnectMethodSymbol = TryLookupFunction(
                syntax,
                "iWare.Database.SqlCommands2",
                "Disconnect");
            Debug.Assert(disconnectMethodSymbol is not null, "iWare.Database.SqlCommands2.Disconnect not found");
            var readMethodSymbol = TryLookupFunction(
                syntax,
                "Microsoft.Data.SqlClient.SqlDataReader",
                "Read");
            Debug.Assert(readMethodSymbol is not null, "Microsoft.Data.SqlClient.SqlDataReader.Read not found");

            // /*LOCALS*/
            // sql {
            //     /*QUERY*/
            // }
            // sqlDo {
            //     /*FOREACH*/
            // }
            // sqlEmpty {
            //     /*EMPTY RESULT*/
            // }
            // sqlEnd {
            //     /*ALWAYS RUN*/
            // }
            // 
            // becomes
            //
            // SqlDataReader? sqlReader = null;
            // try
            // {
            //     /*LOCALS -- declare*/
            //     sqlReader = Connect(/*QUERY*/);
            //     if (sqlReader.Read())
            //     {
            //         if (sqlDoAction is not null)
            //         {
            //             do
            //             {
            //                 /*LOCALS -- assign*/
            //                 /*FOREACH*/
            //             } while (sqlReader.Read());
            //         }
            //     }
            //     else
            //     {
            //         if (sqlEmptyAction is not null)
            //         {
            //             /*EMPTY RESULT*/
            //         }
            //     }
            // }
            // finally
            // {
            //     Disconnect(sqlReader);
            //     if (sqlEndAction is not null)
            //     {
            //         /*ALWAYS RUN*/
            //     }
            // }

            // SqlDataReader? sqlReader = null;
            var readerLocalSymbol = _factory.SynthesizedLocal(sqlDataReaderType);
            var sqlReaderAssignmentStatement = _factory.ExpressionStatement(
                _factory.AssignmentExpression(
                    _factory.Local(readerLocalSymbol),
                    _factory.Null(sqlDataReaderType)));

            // sqlReader = Connect(/*QUERY*/);
            var sqlReaderConnectStatement = _factory.ExpressionStatement(
                _factory.AssignmentExpression(
                    _factory.Local(readerLocalSymbol),
                    _factory.Call(
                        null,
                        connectMethodSymbol,
                        ImmutableArray.Create<BoundExpression>(sqlTextBoundLiteral))));

            // sqlReader.Read()
            var readCall = _factory.Call(_factory.Local(readerLocalSymbol), readMethodSymbol);

            // do
            // {
            //     /*LOCALS -- assign*/
            //     /*FOREACH*/
            // } while (sqlReader.Read());
            // or empty block if sqlDo is null
            BoundStatement sqlDoLoop;
            if (sqlDoBlock != null)
            {
                Debug.Assert(sqlDoSymbols is not null, "recordSymbol should be defined if sqlDoBlock is not null");
                //var recordSymbol = ((ImmutableArray<LocalSymbol>)sqlDoSymbols).First();
                var recordTemp = _factory.SynthesizedLocal(iDataRecordType);
                //var assignRecord = _factory.ExpressionStatement(
                //    _factory.AssignmentExpression(
                //        _factory.Local(recordSymbol),
                //        _factory.Convert(
                //            iDataRecordType,
                //            _factory.Local(readerLocalSymbol))));
                var assignRecord = _factory.ExpressionStatement(
                    _factory.AssignmentExpression(
                        _factory.Local(recordTemp),
                        _factory.Convert(
                            iDataRecordType,
                            _factory.Local(readerLocalSymbol))));
                //BoundStatement rewrittenDoBlock = RewriteBoundBlock(
                //    sqlDoBlock,
                //    recordSymbol,
                //    recordTemp);

                var startLabel = new GeneratedLabelSymbol("sqlDoStart");
                var conditionalGoto = _factory.ConditionalGoto(
                    readCall,
                    startLabel,
                    true);
                sqlDoLoop = _factory.Block(
                    [recordTemp],
                    ImmutableArray.Create(
                        assignRecord,
                        _factory.Label(startLabel),
                        sqlDoBlock,
                        conditionalGoto));
            }
            else
            {
                sqlDoLoop = _factory.Block();
            }

            // /*EMPTY RESULT*/
            // or empty block if sqlEmpty is null
            BoundStatement sqlEmptyStatement;
            if (sqlEmptyBlock != null)
            {
                sqlEmptyStatement = _factory.Block(sqlEmptyBlock);
            }
            else
            {
                sqlEmptyStatement = _factory.Block();
            }

            // if (sqlReader.Read()) { ... } else { ... }
            var ifRead = _factory.If(readCall, sqlDoLoop, sqlEmptyStatement);

            // Disconnect(sqlReader);
            var sqlReaderDisconnectStatement = _factory.ExpressionStatement(
                _factory.Call(
                    null,
                    disconnectMethodSymbol,
                    ImmutableArray.Create<BoundExpression>(_factory.Local(readerLocalSymbol))));

            // /*ALWAYS RUN*/
            // or empty block if sqlEnd is null
            BoundStatement sqlEndStatement;
            if (sqlEndBlock != null)
            {
                sqlEndStatement = _factory.Block(sqlEndBlock);
            }
            else
            {
                sqlEndStatement = _factory.Block();
            }

            // try { ... } finally { ... }
            var finallyBlock = _factory.Block(sqlReaderDisconnectStatement, sqlEndStatement);
            var tryBlock = _factory.Block(sqlReaderConnectStatement, ifRead);
            var finallyLabel = new GeneratedLabelSymbol("sqlFinally");
            var tryFinallyStatement = _factory.Try(tryBlock, [], finallyBlock, finallyLabel);

            return BoundStatementList.Synthesized(
                node.Syntax,
                new BoundBlock(
                    syntax,
                    [readerLocalSymbol],
                    [
                        sqlReaderAssignmentStatement,
                        tryFinallyStatement
                    ]));
        }

        private BoundStatement RewriteBoundBlock(
            BoundBlock block,
            LocalSymbol placeholder,
            LocalSymbol replacement)
        {
            var rewriter = new ReplaceLocalRewriter(placeholder, replacement, _factory);
            return (BoundBlock)rewriter.VisitBlock(block)!;
        }

        private class ReplaceLocalRewriter(LocalSymbol placeholder, LocalSymbol replacement, SyntheticBoundNodeFactory factory) : BoundTreeRewriter
        {
            private readonly LocalSymbol _placeholder = placeholder;
            private readonly LocalSymbol _replacement = replacement;
            private readonly SyntheticBoundNodeFactory _factory = factory;

            public override BoundNode? VisitLocal(BoundLocal node)
            {
                if (node.LocalSymbol == _placeholder)
                {
                    return _factory.Local(_replacement);
                }

                return base.VisitLocal(node);
            }

            protected override BoundNode? VisitExpressionOrPatternWithoutStackGuard(BoundNode node)
            {
                throw new System.NotImplementedException();
            }
        }



        private MethodSymbol? TryLookupFunction(SyntaxNode syntax, string @namespace, string functionName)
        {
            var type = _compilation.GetTypeByMetadataName(@namespace);
            var myFunction = type?
                .GetMembers(functionName)
                .OfType<MethodSymbol>()
                .FirstOrDefault();
            return myFunction;
        }
    }
}
