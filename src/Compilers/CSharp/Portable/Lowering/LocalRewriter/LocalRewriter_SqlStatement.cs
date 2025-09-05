// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
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
                node.querySymbols,
                node.queryNames);
        }

        private BoundStatement RewriteSqlStatement(
            BoundNode node,
            BoundLiteral sqlTextBoundLiteral,
            BoundBlock? sqlDoBlock,
            BoundBlock? sqlEmptyBlock,
            BoundBlock? sqlEndBlock,
            Dictionary<string, Symbol> querySymbols,
            Dictionary<string, string> queryNames)
        {
            var syntax = node.Syntax;

            // Types
            var iDataRecordType = _compilation.GetTypeByMetadataName("System.Data.IDataRecord");
            Debug.Assert(iDataRecordType is not null, "System.Data.IDataRecord not found");
            var sqlDataReaderType = _compilation.GetTypeByMetadataName("Microsoft.Data.SqlClient.SqlDataReader");
            Debug.Assert(sqlDataReaderType is not null, "Microsoft.Data.SqlClient.SqlDataReader not found");
            var stringType = _compilation.GetSpecialType(SpecialType.System_String);
            var stringStringDictionaryType = _compilation
                .GetTypeByMetadataName("System.Collections.Generic.Dictionary`2")!
                .Construct(
                    stringType,
                    stringType);
            Debug.Assert(stringStringDictionaryType is not null, "Dictionary<string, string> not found");
            var stringObjectDictionaryType = _compilation
                .GetTypeByMetadataName("System.Collections.Generic.Dictionary`2")!
                .Construct(
                    stringType,
                    _compilation.GetSpecialType(SpecialType.System_Object));
            Debug.Assert(stringObjectDictionaryType is not null, "Dictionary<string, object> not found");

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
                "iWare.Database.SqlCommands2",
                "Read");
            Debug.Assert(readMethodSymbol is not null, "iWare.Database.SqlCommands2.Read not found");

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
            // Dictionary<string, object?>? readValues = null;
            // Dictionary<string, string> nameValues = queryNames
            // SqlDataReader? sqlReader = null;
            // /*READ*/ readValues = Read(sqlReader, nameValues)
            // try
            // {
            //     sqlReader = Connect(/*QUERY*/);
            //     if (/*READ*/ != null)
            //     {
            //         if (sqlDoAction is not null)
            //         {
            //             do
            //             {
            //                 /*ASSIGN LOCALS*/
            //                 /*FOREACH*/
            //             } while ((/*READ*/) != null);
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

            // Dictionary<string, object?>? readValues = null;
            var readValuesSymbol = _factory.SynthesizedLocal(stringObjectDictionaryType);
            var readValuesLocal = _factory.Local(readValuesSymbol);
            var readValuesAssignmentStatement = _factory.ExpressionStatement(
                _factory.AssignmentExpression(
                    readValuesLocal,
                    _factory.Null(stringObjectDictionaryType)));

            // Dictionary<string, string> nameValues = queryNames;
            var nameValuesLocal = _factory.StoreToTemp(_factory.New(stringStringDictionaryType, ImmutableArray<BoundExpression>.Empty), out var nameValuesBoundDeclaration);
            var nameValuesDeclarationStatement = _factory.ExpressionStatement(nameValuesBoundDeclaration);
            var add = stringStringDictionaryType
                .GetMembers("Add")
                .OfType<MethodSymbol>()
                .Single(
                    m => m.Parameters.Length == 2 &&
                    m.Parameters[0].Type.Equals(stringType) &&
                    m.Parameters[1].Type.Equals(stringType));
            var nameValuesAssignmentStatementsBuilder = ImmutableArray.CreateBuilder<BoundStatement>();
            foreach (var kvp in queryNames)
            {
                var keyLiteral = _factory.Literal(ConstantValue.Create(kvp.Key), stringType);
                var valueLiteral = _factory.Literal(ConstantValue.Create(kvp.Value), stringType);

                var callAdd = _factory.Call(
                    nameValuesLocal,
                    add,
                    ImmutableArray.Create<BoundExpression>(keyLiteral, valueLiteral));

                nameValuesAssignmentStatementsBuilder.Add(_factory.ExpressionStatement(callAdd));
            }
            var nameValuesAssignmentStatements = nameValuesAssignmentStatementsBuilder.ToImmutableArray();

            // SqlDataReader? sqlReader = null;
            var sqlReaderSymbol = _factory.SynthesizedLocal(sqlDataReaderType);
            var sqlReaderLocal = _factory.Local(sqlReaderSymbol);
            var sqlReaderAssignmentStatement = _factory.ExpressionStatement(
                _factory.AssignmentExpression(
                    sqlReaderLocal,
                    _factory.Null(sqlDataReaderType)));

            // sqlReader = Connect(/*QUERY*/);
            var sqlReaderConnectStatement = _factory.ExpressionStatement(
                _factory.AssignmentExpression(
                    sqlReaderLocal,
                    _factory.Call(
                        null,
                        connectMethodSymbol,
                        ImmutableArray.Create<BoundExpression>(sqlTextBoundLiteral))));

            // /*READ*/ readValues = Read(sqlReader, nameValues)
            // Dictionary<string, object?>? Read(SqlDataReader sqlReader, Dictionary<string, string> queryNames)
            var readCall = _factory.AssignmentExpression(readValuesLocal,
                _factory.Call(
                    null,
                    readMethodSymbol,
                    ImmutableArray.Create<BoundExpression>(
                        sqlReaderLocal,
                        nameValuesLocal)));

            // foreach (var sym in querySymbols)
            // {
            //     if (readValues.TryGetValue(sym.Key, var tmp))
            //     {
            //         sym.Value = tmp;
            //     }
            // }
            var assignLocalsStatementsBuilder = ImmutableArray.CreateBuilder<BoundStatement>();
            foreach (var sym in querySymbols)
            {
                // readValues.TryGetValue(sym.Key, var tmp)
                var symbol = sym.Value;
                var targetType = symbol switch
                {
                    LocalSymbol l => l.Type,
                    FieldSymbol f => f.Type,
                    PropertySymbol p => p.Type,
                    _ => _compilation.GetSpecialType(SpecialType.System_Object)
                };
                var tryGetTmpSymbol = _factory.SynthesizedLocal(targetType);
                var tryGetTmpLocal = _factory.Local(tryGetTmpSymbol);
                var tryGetValueMethod = readValuesSymbol.Type
                    .GetMembers("TryGetValue")
                    .OfType<MethodSymbol>()
                    .First();

                var indexerAccess = _factory.Call(
                    readValuesLocal,
                    tryGetValueMethod,
                    [_factory.Literal(sym.Key), tryGetTmpLocal]);

                BoundExpression lhs = symbol switch
                {
                    LocalSymbol l => _factory.Local(l),
                    FieldSymbol f => _factory.Field(null, f),
                    PropertySymbol p => _factory.Property(null, p),
                    _ => throw ExceptionUtilities.UnexpectedValue(symbol.Kind)
                };

                // if (readValues.TryGetValue(sym.Key, var tmp))
                assignLocalsStatementsBuilder.Add(
                    _factory.Block([tryGetTmpSymbol],
                        _factory.If(
                            indexerAccess,
                            _factory.ExpressionStatement(
                                _factory.AssignmentExpression(lhs, tryGetTmpLocal)))));
            }
            var assignLocalsStatements = _factory.Block(assignLocalsStatementsBuilder.ToImmutableArray());

            // do
            // {
            //     /*ASSIGN LOCALS*/
            //     /*FOREACH*/
            // } while ((/*READ*/) != null);
            // or empty block if sqlDo is null
            BoundStatement sqlDoLoop;
            if (sqlDoBlock != null)
            {
                var sqlDoLoopCondition = _factory.ObjectNotEqual(
                    readCall,
                    _factory.Null(stringObjectDictionaryType));
                var startLabel = new GeneratedLabelSymbol("sqlDoStart");
                var conditionalGoto = _factory.ConditionalGoto(
                    sqlDoLoopCondition,
                    startLabel,
                    true);
                sqlDoLoop = _factory.Block(
                    ImmutableArray.Create(
                        _factory.Label(startLabel),
                        assignLocalsStatements,
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

            // if (/*READ*/ != null) { ... } else { ... }
            var ifRead = _factory.If(
                _factory.ObjectNotEqual(
                    readCall,
                    _factory.Null(stringObjectDictionaryType)),
                sqlDoLoop,
                sqlEmptyStatement);

            // Disconnect(sqlReader);
            var sqlReaderDisconnectStatement = _factory.ExpressionStatement(
                _factory.Call(
                    null,
                    disconnectMethodSymbol,
                    ImmutableArray.Create<BoundExpression>(sqlReaderLocal)));

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
                    [sqlReaderSymbol, readValuesSymbol, nameValuesLocal.LocalSymbol],
                    [
                        sqlReaderAssignmentStatement,
                        readValuesAssignmentStatement,
                        nameValuesDeclarationStatement,
                        ..nameValuesAssignmentStatements,
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
