// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed partial class LocalRewriter
    {
        // Builtin types
        private NamedTypeSymbol stringType;
        private NamedTypeSymbol objectType;
        private NamedTypeSymbol immutableArrayType;
        private NamedTypeSymbol nullableType;
        // External types
        private NamedTypeSymbol sqlDataReaderType;
        private NamedTypeSymbol iDataReaderType;
        // Combined types
        private NamedTypeSymbol nullableObjectType;
        private NamedTypeSymbol nullableImmutableArrayType;
        private NamedTypeSymbol immutableArrayOfNullableObjectsType;
        private NamedTypeSymbol immutableArrayOfStringsType;
        private NamedTypeSymbol immutableArrayOfStringsBuilderType;
        private NamedTypeSymbol immutableArrayBuilderType;
        private NamedTypeSymbol staticImmutableArrayType;
        // Methods
        private MethodSymbol immutableArrayOfStringsBuilderMethodSymbol;
        private MethodSymbol immutableArrayOfStringsBuilderAddMethodSymbol;
        private MethodSymbol immutableArrayOfStringsBuilderToImmutableArrayMethodSymbol;
        private MethodSymbol arrayIsDefaultOrEmptyMethodSymbol;
        // Functions
        private MethodSymbol connectMethodSymbol;
        private MethodSymbol disconnectMethodSymbol;
        private MethodSymbol readMethodSymbol;

        public override BoundNode? VisitSqlStatement(BoundSqlStatement node)
        {
            var sqlTextBoundLiteral = _factory.Literal(node.SqlContents);

            BoundBlock? sqlDoBoundBlock = null;
            if (node.SqlDoOpt is not null)
            {
                sqlDoBoundBlock = (BoundBlock)VisitBlock(node.SqlDoOpt.Body);
            }
            BoundBlock? sqlEmptyBoundBlock = null;
            if (node.SqlEmptyOpt is not null)
            {
                sqlEmptyBoundBlock = (BoundBlock)VisitBlock(node.SqlEmptyOpt.Body);
            }
            BoundBlock? sqlEndBoundBlock = null;
            if (node.SqlEndOpt is not null)
            {
                sqlEndBoundBlock = (BoundBlock)VisitBlock(node.SqlEndOpt.Body);
            }

            init(node.Syntax);

            //TODO-aljaz do I still even need queryNames outside of Binder_Statements.cs?
            return RewriteSqlStatement(
                node,
                sqlTextBoundLiteral,
                sqlDoBoundBlock,
                sqlEmptyBoundBlock,
                sqlEndBoundBlock,
                node.querySymbols,
                //node.queryNames,
                node.querySqlNames);
        }

        private void init(SyntaxNode syntax)
        {
            // Types

            // Builtin types
            stringType = _compilation.GetSpecialType(SpecialType.System_String);
            objectType = _compilation.GetSpecialType(SpecialType.System_Object);
            immutableArrayType = _compilation.GetWellKnownType(WellKnownType.System_Collections_Immutable_ImmutableArray_T);
            nullableType = _compilation.GetSpecialType(SpecialType.System_Nullable_T);

            // External types
            sqlDataReaderType = _compilation.GetTypeByMetadataName("Microsoft.Data.SqlClient.SqlDataReader")!;
            Debug.Assert(sqlDataReaderType is not null, "type Microsoft.Data.SqlClient.SqlDataReader not found");

            iDataReaderType = _compilation.GetTypeByMetadataName("System.Data.IDataReader")!;
            Debug.Assert(iDataReaderType is not null, "type System.Data.IDataReader not found");

            // Combined types
            nullableObjectType = nullableType.Construct(objectType);
            Debug.Assert(nullableObjectType is not null, "type object? not found");

            nullableImmutableArrayType = nullableType.Construct(immutableArrayType);
            Debug.Assert(nullableImmutableArrayType is not null, "type ImmutableArray<T>? not found");

            immutableArrayOfNullableObjectsType = immutableArrayType.Construct(nullableObjectType);
            Debug.Assert(immutableArrayOfNullableObjectsType is not null, "type ImmutableArray<object?> not found");

            //var nullableImmutableArrayOfNullableObjectsType = nullableType.Construct(immutableArrayOfNullableObjectsType);
            //Debug.Assert(nullableImmutableArrayOfNullableObjectsType is not null, "type ImmutableArray<object?>? not found");

            immutableArrayOfStringsType = immutableArrayType
                .Construct(stringType);
            Debug.Assert(immutableArrayOfStringsType is not null, "type ImmutableArray<string> not found");

            immutableArrayOfStringsBuilderType = immutableArrayOfStringsType.GetTypeMembers("Builder").Single();
            Debug.Assert(immutableArrayOfStringsBuilderType is not null, "type ImmutableArray<string>.Builder not found");

            immutableArrayBuilderType = immutableArrayType.GetTypeMembers("Builder").Single();
            Debug.Assert(immutableArrayBuilderType is not null, "type ImmutableArray<T>.Builder not found");

            staticImmutableArrayType = _compilation.GetTypeByMetadataName("System.Collections.Immutable.ImmutableArray")!;
            Debug.Assert(staticImmutableArrayType is not null, "type ImmutableArray class not found");

            // Methods
            immutableArrayOfStringsBuilderMethodSymbol = staticImmutableArrayType
                .GetMembers("CreateBuilder")
                .OfType<MethodSymbol>()
                .Single(m => m.Parameters.Length == 0)
                .Construct(stringType);
            Debug.Assert(immutableArrayOfStringsBuilderMethodSymbol is not null, "method ImmutableArray.CreateBuilder<string>() not found");

            immutableArrayOfStringsBuilderAddMethodSymbol = immutableArrayOfStringsBuilderType
                .GetMembers("Add")
                .OfType<MethodSymbol>()
                .Single(
                    m => m.Parameters.Length == 1 &&
                    m.Parameters[0].Type.Equals(stringType));
            Debug.Assert(immutableArrayOfStringsBuilderAddMethodSymbol is not null, "method ImmutableArray.Builder<string>.Add() not found");

            immutableArrayOfStringsBuilderToImmutableArrayMethodSymbol = staticImmutableArrayType
                .GetMembers("ToImmutableArray")
                .OfType<MethodSymbol>()
                .Single(
                    m => m.Parameters.Length == 1 &&
                    m.Parameters[0].Type.OriginalDefinition.Equals(immutableArrayBuilderType))
                .Construct(stringType);
            Debug.Assert(immutableArrayOfStringsBuilderToImmutableArrayMethodSymbol is not null, "method ImmutableArray.Builder<string>.ToImmutableArray() not found");

            // Functions
            connectMethodSymbol = TryLookupFunction(
                syntax,
                "iWare.Database.SqlCommands2",
                "Connect")!;
            Debug.Assert(connectMethodSymbol is not null, "method iWare.Database.SqlCommands2.Connect not found");

            disconnectMethodSymbol = TryLookupFunction(
                syntax,
                "iWare.Database.SqlCommands2",
                "Disconnect")!;
            Debug.Assert(disconnectMethodSymbol is not null, "method iWare.Database.SqlCommands2.Disconnect not found");

            readMethodSymbol = TryLookupFunction(
                syntax,
                "iWare.Database.SqlCommands2",
                "Read")!;
            Debug.Assert(readMethodSymbol is not null, "method iWare.Database.SqlCommands2.Read() not found");
            Debug.Assert(
                readMethodSymbol.Parameters.Length == 2 &&
                readMethodSymbol.Parameters[0].Type.Equals(sqlDataReaderType) &&
                readMethodSymbol.Parameters[1].Type.Equals(immutableArrayOfStringsType),
                "method iWare.Database.SqlCommands2.Read() does not match expected signature");
        }

        private BoundStatement RewriteSqlStatement(
            BoundNode node,
            BoundLiteral sqlTextBoundLiteral,
            BoundBlock? sqlDoBoundBlock,
            BoundBlock? sqlEmptyBoundBlock,
            BoundBlock? sqlEndBoundBlock,
            ImmutableArray<Symbol> querySymbols,
            //ImmutableArray<string> queryNames,
            ImmutableArray<string> querySqlNames)
        {
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
            // ImmutableArray<object?>? readValues = null;
            // ImmutableArray<string> sqlNameValues = querySqlNames
            // SqlDataReader? sqlReader = null;
            // /*READ*/ readValues = Read(sqlReader, sqlNameValues)
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

            var syntax = node.Syntax;
            var readMethodTargetType = readMethodSymbol.ReturnType;
            var connectMethodTargetType = connectMethodSymbol.ReturnType;
            var sideEffects = ImmutableArray.CreateBuilder<BoundStatement>();
            arrayIsDefaultOrEmptyMethodSymbol = readMethodTargetType.GetMembers("get_IsDefaultOrEmpty").OfType<MethodSymbol>().Single();

            // SqlDataReader? sqlReader = null;
            var sqlReaderSymbol = _factory.SynthesizedLocal(connectMethodTargetType);
            var sqlReaderLocal = _factory.Local(sqlReaderSymbol);
            sideEffects.Add(
                _factory.Assignment(
                    sqlReaderLocal,
                    _factory.Null(sqlDataReaderType)));

            // ImmutableArray<object?>? readValues = null;
            var readValuesSymbol = _factory.SynthesizedLocal(readMethodTargetType);
            //var readValuesSymbol = _factory.SynthesizedLocal(immutableArrayOfNullableObjectsType);
            var readValuesLocal = _factory.Local(readValuesSymbol);
            //sideEffects.Add(
            //    _factory.Assignment(
            //        readValuesLocal,
            //        _factory.Null(readMethodTargetType)));

            // ImmutableArray<string> sqlNameValues = sqlQueryNames;
            var sqlNamesSymbol = _factory.SynthesizedLocal(immutableArrayOfStringsType);
            var sqlNamesLocal = _factory.Local(sqlNamesSymbol);
            sideEffects.Add(AssignSqlNames(sqlNamesLocal, querySqlNames));

            // sqlReader = Connect(/*QUERY*/);
            var sqlReaderConnectStatement = _factory.Assignment(
                sqlReaderLocal,
                _factory.Call(
                    null,
                    connectMethodSymbol,
                    ImmutableArray.Create<BoundExpression>(sqlTextBoundLiteral)));

            //  /*ASSIGN LOCALS*/
            //  for (int i .. querySymbols.Length)
            //  {
            //      var symbol = querySymbols[i];
            //      var tmp = readValues[i];
            //      if (tmp != null)
            //      {
            //          querySymbols[i] = tmp;
            //      }
            //  }
            var assignLocalsStatements = AssignLocals(querySymbols, readValuesLocal);

            // do
            // {
            //     /*ASSIGN LOCALS*/
            //     /*FOREACH*/
            // } while ((/*READ*/) != null);
            // or empty block if sqlDo is null
            BoundStatement sqlDoLoop = CreateSqlDoLoop(
                assignLocalsStatements,
                sqlDoBoundBlock,
                readValuesLocal,
                sqlReaderLocal,
                sqlNamesLocal,
                readMethodTargetType);

            // /*EMPTY RESULT*/
            // or empty block if sqlEmpty is null
            BoundStatement sqlEmptyStatement;
            if (sqlEmptyBoundBlock != null)
            {
                sqlEmptyStatement = _factory.Block(sqlEmptyBoundBlock);
            }
            else
            {
                sqlEmptyStatement = _factory.Block();
            }

            // if (/*READ*/ != null) { ... } else { ... }
            var readCallStatement = _factory.ExpressionStatement(CreateReadCall(readValuesLocal, sqlReaderLocal, sqlNamesLocal));
            var ifRead = _factory.If(
                _factory.Not(_factory.Call(readValuesLocal, arrayIsDefaultOrEmptyMethodSymbol)),
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
            if (sqlEndBoundBlock != null)
            {
                sqlEndStatement = _factory.Block(sqlEndBoundBlock);
            }
            else
            {
                sqlEndStatement = _factory.Block();
            }

            // try { ... } finally { ... }
            var finallyBlock = _factory.Block(sqlReaderDisconnectStatement, sqlEndStatement);
            var tryBlock = _factory.Block(sqlReaderConnectStatement, readCallStatement, ifRead);
            var finallyLabel = new GeneratedLabelSymbol("sqlFinally");
            sideEffects.Add(_factory.Try(tryBlock, [], finallyBlock, finallyLabel));

            var sideEffectsImmutable = sideEffects.ToImmutableArray();
            var ret = _factory.Block(
                [sqlReaderSymbol, readValuesSymbol, sqlNamesSymbol/*, nameValuesLocal.LocalSymbol*/],
                sideEffectsImmutable);
            return ret;
        }

        private BoundExpression CreateReadCall(BoundLocal readValuesLocal, BoundLocal sqlReaderLocal, BoundLocal sqlNamesLocal)
        {
            // /*READ*/ readValues = Read(sqlReader, nameValues)
            // ImmutableArray<object?>? Read(IDataReader reader, ImmutableArray<string> querySqlNames)
            //TODO-aljaz wrap in _factory.ExpressionStatement?
            return _factory.AssignmentExpression(
                readValuesLocal,
                _factory.Call(
                    null,
                    readMethodSymbol,
                    ImmutableArray.Create<BoundExpression>(
                        sqlReaderLocal,
                        sqlNamesLocal)));
        }

        private BoundStatement AssignSqlNames(BoundLocal sqlNamesLocal, ImmutableArray<string> names)
        {
            var sideEffects = ImmutableArray.CreateBuilder<BoundStatement>();

            //var builder = ImmutableArray.Builder<string>();
            var builderSymbol = _factory.SynthesizedLocal(immutableArrayOfStringsBuilderType);
            var builderLocal = _factory.Local(builderSymbol);
            sideEffects.Add(_factory.Assignment(builderLocal, _factory.Call(null, immutableArrayOfStringsBuilderMethodSymbol)));

            //foreach name add
            foreach (var name in names)
            {
                sideEffects.Add(
                    _factory.ExpressionStatement(
                        _factory.Call(
                            builderLocal,
                            immutableArrayOfStringsBuilderAddMethodSymbol,
                            _factory.Literal(name))));
            }

            //sqlNamesLocal = builder.ToImmutableArray();
            sideEffects.Add(_factory.Assignment(
                sqlNamesLocal,
                _factory.Call(
                    null,
                    immutableArrayOfStringsBuilderToImmutableArrayMethodSymbol,
                    builderLocal)));

            return _factory.Block(
                [builderSymbol],
                sideEffects.ToImmutableArray());
        }

        private BoundStatement CreateSqlDoLoop(BoundStatement assignLocalsStatements, BoundBlock? sqlDoBoundBlock, BoundLocal readValuesLocal, BoundLocal sqlReaderLocal, BoundLocal sqlNamesLocal, TypeSymbol targetType)
        {
            if (sqlDoBoundBlock != null)
            {
                var readCallStatement = _factory.ExpressionStatement(CreateReadCall(readValuesLocal, sqlReaderLocal, sqlNamesLocal));
                var startLabel = new GeneratedLabelSymbol("sqlDoStart");
                var conditionalGoto = _factory.ConditionalGoto(
                    _factory.Not(_factory.Call(readValuesLocal, arrayIsDefaultOrEmptyMethodSymbol)),
                    startLabel,
                    true);
                return _factory.Block(
                    ImmutableArray.Create(
                        _factory.Label(startLabel),
                        assignLocalsStatements,
                        sqlDoBoundBlock,
                        readCallStatement,
                        conditionalGoto));
            }
            else
            {
                return _factory.Block();
            }
        }

        BoundStatement AssignLocals(ImmutableArray<Symbol> querySymbols, BoundExpression readValuesLocal)
        {
            var sideEffects = ImmutableArray.CreateBuilder<BoundStatement>();
            var tmpSymbol = _factory.SynthesizedLocal(objectType);
            var tmpLocal = _factory.Local(tmpSymbol);
            var getItemMethodSymbol =
                readValuesLocal.Type!
                .GetMembers("get_Item")
                .OfType<MethodSymbol>()
                .FirstOrDefault();
            Debug.Assert(getItemMethodSymbol is not null, "ImmutableArray<object>.get_Item() not found");
            Debug.Assert(
                getItemMethodSymbol.Parameters.Length == 1,
                "ImmutableArray<object>.get_Item() not found");
            for (int i = 0; i < querySymbols.Length; i++)
            {
                //  var symbol = querySymbols[i];
                var symbol = querySymbols[i];
                var targetType = symbol switch
                {
                    LocalSymbol l => l.Type,
                    FieldSymbol f => f.Type,
                    PropertySymbol p => p.Type,
                    _ => _compilation.GetSpecialType(SpecialType.System_Object)
                };
                //  var tmp = readValues[i];
                var assignTmp = _factory.Assignment(
                    tmpLocal,
                    _factory.Call(
                        readValuesLocal,
                        getItemMethodSymbol,
                        [_factory.Literal(i)]));

                var conversion = _factory.Convert(
                    targetType,
                    tmpLocal); //_factory.Convert(_compilation.GetSpecialType(SpecialType.System_Object), tmpLocal));

                BoundExpression lhs = symbol switch
                {
                    LocalSymbol l => _factory.Local(l),
                    FieldSymbol f => _factory.Field(null, f),
                    PropertySymbol p => _factory.Property(null, p),
                    _ => throw ExceptionUtilities.UnexpectedValue(symbol.Kind)
                };

                // if (tmp != null)
                sideEffects.AddRange(
                    assignTmp,
                    _factory.If(
                        _factory.ObjectNotEqual(tmpLocal, _factory.Null(nullableObjectType)),
                        _factory.Assignment(lhs, conversion))
                    );
            }
            return _factory.Block([tmpSymbol], sideEffects.ToImmutableArray());
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
