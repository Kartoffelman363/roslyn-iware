// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Data;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed partial class LocalRewriter
    {
        //public override BoundNode? VisitSqlStatement(BoundSqlStatement node)
        //{
        //    return base.VisitSqlStatement(node);
        //}

        public override BoundNode? VisitSqlStatement(BoundSqlStatement node)
        {
            var sqlTextBoundLiteral = _factory.Literal(node.SqlContents);

            BoundStatement? sqlDoBoundLiteral = null;
            if (node.SqlDoOpt is not null)
            {
                //if (node.SqlDoOpt.Body.Kind == BoundKind.Block)
                //{
                //    sqlDoBoundLiteral = (BoundStatement)VisitBlock((BoundBlock)node.SqlDoOpt.Body);
                //}
                //else if (node.SqlDoOpt.Body.Kind == BoundKind.ExpressionStatement)
                //{
                //    sqlDoBoundLiteral = (BoundStatement)VisitExpressionStatement((BoundExpressionStatement)node.SqlDoOpt.Body);
                //}
                sqlDoBoundLiteral = VisitStatement(node.SqlDoOpt.Body);
            }

            return RewriteSqlStatement(node, sqlTextBoundLiteral, sqlDoBoundLiteral, null, null);

            //// Types
            //var sqlEmptyType = _compilation.GetWellKnownType(WellKnownType.System_Action);
            //var iDataRecordType = _compilation.GetTypeByMetadataName("System.Data.IDataRecord");
            //var boolType = _compilation.GetSpecialType(SpecialType.System_Boolean);
            //Debug.Assert(iDataRecordType is not null, "Could not find type System.Data.IDataRecord");
            //var sqlDoType = _compilation.GetWellKnownType(WellKnownType.System_Action_T2).Construct(
            //        iDataRecordType,
            //        boolType);
            //
            //// sqlDo
            //BoundExpression sqlDoBoundLiteral = _factory.Null(sqlDoType);
            //if (node.SqlDoOpt is not null)
            //{
            //    //    Debug.Assert(node.SqlDoOpt.Kind != BoundKind.UnboundLambda, "Can't be an unbound lambda");
            //    //    sqlDoBoundLiteral = (BoundExpression)Visit(node.SqlDoOpt)!;
            //    var sqlDoBlock = _factory.Block(node.SqlDoOpt.Body);
            //    var recordParam = _factory.SynthesizedParameter(iDataRecordType, "record");
            //    var firstLineParam = _factory.SynthesizedParameter(boolType, "firstLine");
            //    //var snyLamSym = _factory.fun
            //    //var n = LambdaSymbol.sy
            //    //var n = _compilation.GetBinder();
            //    //var p = new BoundDelegateCreationExpression(
            //    //    );
            //    var actionFunctionType = new FunctionTypeSymbol(sqlDoType);
            //    var actionMethodSymbol = _compilation
            //        .GetWellKnownType(WellKnownType.System_Action_T2)
            //        .GetMembers()
            //        .OfType<MethodSymbol>()
            //        .FirstOrDefault();
            //    var lams = new ConstructedMethodSymbol(
            //        actionMethodSymbol,
            //        new ImmutableArray<TypeWithAnnotations>());
            //    var ulam = new UnboundLambda(
            //        node.SqlDoOpt.Syntax,
            //        null,
            //        actionFunctionType,
            //        false,
            //        false);
            //    //var mlam = UnboundLambda.Create(
            //    //    node.SqlDoOpt.Syntax,
            //    //    n,
            //    //    )
            //    var lam = new BoundLambda(
            //        node.SqlDoOpt.Syntax,
            //        null,
            //        lams,
            //        sqlDoBlock,
            //        ReadOnlyBindingDiagnostic<AssemblySymbol>.Empty,
            //        null,
            //        null
            //        );
            //}
            //
            //var sqlEmptyBoundLiteral = _factory.Null(sqlEmptyType);
            //var sqlEndBoundLiteral = _factory.Null(sqlEmptyType);
            //
            //var mySqlMethod = tryLookupMySqlFunction(node.Syntax);
            //Debug.Assert(mySqlMethod is not null, "iWare.Database.SqlCommands.SqlCommand is missing");
            //
            //var boundCall = _factory.Call(
            //    receiver: null,
            //    method: mySqlMethod,
            //    args: ImmutableArray.Create(sqlTextBoundLiteral, sqlDoBoundLiteral, sqlEmptyBoundLiteral, sqlEndBoundLiteral)
            //    );
            //
            //return new BoundExpressionStatement(node.Syntax, boundCall);
            //
            //MethodSymbol? tryLookupMySqlFunction(SyntaxNode syntax)
            //{
            //    return TryLookupFunction(syntax, "iWare.Database.SqlCommands", "SqlCommand");
            //}
        }

        private BoundStatement RewriteSqlStatement(
            BoundNode node,
            BoundLiteral sqlTextBoundLiteral,
            BoundStatement? sqlDoBlock,
            BoundStatement? sqlEmptyBlock,
            BoundStatement? sqlEndBlock)
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

            //var lab = new GeneratedLabelSymbol("");

            // SqlDataReader? sqlReader = null;
            var readerLocalSymbol = _factory.SynthesizedLocal(sqlDataReaderType, syntax);
            var readerLocal = _factory.Local(readerLocalSymbol);
            var sqlReaderAssignmentStatement = _factory.ExpressionStatement(
                _factory.AssignmentExpression(
                    readerLocal,
                    _factory.Null(sqlDataReaderType)));

            // sqlReader = Connect(/*QUERY*/);
            var sqlReaderConnectStatement = _factory.ExpressionStatement(
                _factory.AssignmentExpression(
                    readerLocal,
                    _factory.Call(
                        null,
                        connectMethodSymbol,
                        ImmutableArray.Create<BoundExpression>(sqlTextBoundLiteral))));

            // sqlReader.Read()
            var readCall = _factory.Call(readerLocal, readMethodSymbol);

            // do
            // {
            //     /*LOCALS -- assign*/
            //     /*FOREACH*/
            // } while (sqlReader.Read());
            // or empty block if sqlDo is null
            BoundStatement sqlDoLoop;
            if (sqlDoBlock != null)
            {
                var startLabel = new GeneratedLabelSymbol("sqlDoStart");
                var conditionalGoto = _factory.ConditionalGoto(
                    readCall,
                    startLabel,
                    true);
                sqlDoLoop = _factory.Block(
                    ImmutableArray.Create(
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
                    ImmutableArray.Create<BoundExpression>(readerLocal)));

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

            //return _factory.Block(
            //    [readerLocalSymbol],
            //    sqlReaderAssignmentStatement,
            //    tryFinallyStatement);

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

        private MethodSymbol? TryLookupFunction(SyntaxNode syntax, string @namespace, string functionName)
        {
            var type = _compilation.GetTypeByMetadataName(@namespace);
            var myFunction = type?
                .GetMembers(functionName)
                .OfType<MethodSymbol>()
                .FirstOrDefault();
            return myFunction;
        }

        /*
        private BoundExpression RewriteSqlDoClause(
            BoundSqlDoClause sqlDo,
            //ImmutableArray<LocalSymbol> locals,
            //BoundStatement rewrittenBody,
            bool hasErrors)
        {
            var syntax = sqlDo.Syntax;
            //    Debug.Assert(node.SqlDoOpt.Kind != BoundKind.UnboundLambda, "Can't be an unbound lambda");
            //sqlDoBoundLiteral = (BoundExpression)Visit(sqlDo)!;
        
            var iDataRecordType = _compilation.GetTypeByMetadataName("System.Data.IDataRecord");
            Debug.Assert(iDataRecordType is not null, "Could not findtype System.Data.IDataRecord");
            var sqlDoType = _compilation.GetWellKnownType(WellKnownType.System_Action_T2).Construct(
                    iDataRecordType,
                    _compilation.GetSpecialType(SpecialType.System_Boolean));
        
        
            //var ulam = new UnboundLambda(
            //    syntax,
            //    null,
            //    sqlDoType,
            //    false);
            //var blam = new BoundLambda(syntax, ulam, sqlDo.Body, di);
            var blk = BoundBlock.Synthesized(
                syntax,
                ImmutableArray.Create(sqlDo.Body));
            var x = new BoundLambda(syntax, null, blk, )
            return BoundCall.Synthesized(syntax, sqlDo.Body, ThreeState.Unknown, MethodSymbol.None);
        }
        */

        //public override BoundNode? VisitSqlDoBlock(BoundSqlDoBlock node)
        //{
        //    Debug.Assert(false, "Here");
        //    var body = Visit(node.Body);
        //    Debug.Assert(body is not null && body.Kind == BoundKind.Block, $"SqlDoBlock.Body is {(body is null ? "null" : $"incorrect kind: {body.Kind.ToString()}")}");
        //    var loweredBody = (BoundBlock)body!;
        //    return new BoundSqlDoBlock(node.Syntax, loweredBody);
        //}
    }
}
