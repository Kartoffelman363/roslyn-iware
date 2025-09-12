// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
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
            return new RewriteSql(_compilation, _factory, node, this).RewriteSqlStatement();
        }

        // This class is just to wrap Type and Method symbols to make things more convenient
        private class RewriteSql
        {
            // Builtin types
            private readonly NamedTypeSymbol _objectType;
            private readonly NamedTypeSymbol _stringType;
            // External types
            private readonly NamedTypeSymbol _sqlDataReaderType;
            private readonly NamedTypeSymbol _immutableArrayType;
            // Combined types
            private readonly NamedTypeSymbol _immutableArrayOfStringsType;
            private readonly NamedTypeSymbol _immutableArrayOfObjectsType;
            // Methods
            private readonly MethodSymbol _immutableArrayBuilderMethodSymbol;
            private readonly MethodSymbol _immutableArrayBuilderToImmutableArrayMethodSymbol;
            private MethodSymbol? _arrayIsDefaultOrEmptyMethodSymbol;
            // Functions
            private readonly MethodSymbol _beginMethodSymbol;
            private readonly MethodSymbol _endMethodSymbol;
            private readonly MethodSymbol _readMethodSymbol;
            // LocalRewriter
            private readonly CSharpCompilation _compilation;
            private readonly SyntheticBoundNodeFactory _factory;
            // Data
            private readonly BoundLiteral _sqlTextBoundLiteral;
            private readonly BoundBlock? _sqlDoBoundBlock;
            private readonly BoundBlock? _sqlEmptyBoundBlock;
            private readonly BoundBlock? _sqlEndBoundBlock;
            private readonly ImmutableArray<Symbol> _querySymbols;
            private readonly ImmutableArray<string> _querySqlNames;
            private readonly ImmutableArray<Symbol> _parameterSymbols;
            private readonly ImmutableArray<string> _parameterNames;
            // Fields
            private readonly FieldSymbol _dbNullValueProperty;

            public RewriteSql(CSharpCompilation compilation, SyntheticBoundNodeFactory factory, BoundSqlStatement node, LocalRewriter localRewriter)
            {
                _compilation = compilation;
                _factory = factory;

                _sqlTextBoundLiteral = _factory.Literal(node.SqlContents);

                _sqlDoBoundBlock = null;
                if (node.SqlDoOpt is not null)
                {
                    _sqlDoBoundBlock = (BoundBlock)localRewriter.VisitBlock(node.SqlDoOpt.Body);
                }
                _sqlEmptyBoundBlock = null;
                if (node.SqlEmptyOpt is not null)
                {
                    _sqlEmptyBoundBlock = (BoundBlock)localRewriter.VisitBlock(node.SqlEmptyOpt.Body);
                }
                _sqlEndBoundBlock = null;
                if (node.SqlEndOpt is not null)
                {
                    _sqlEndBoundBlock = (BoundBlock)localRewriter.VisitBlock(node.SqlEndOpt.Body);
                }

                _querySymbols = node.querySymbols;
                _querySqlNames = node.querySqlNames;
                _parameterSymbols = node.parameterSymbols;
                _parameterNames = node.parameterNames;

                // Builtin types
                _stringType = _compilation.GetSpecialType(SpecialType.System_String);
                _objectType = _compilation.GetSpecialType(SpecialType.System_Object);
                _immutableArrayType = _compilation.GetWellKnownType(WellKnownType.System_Collections_Immutable_ImmutableArray_T);

                // External types
                _sqlDataReaderType = _compilation.GetTypeByMetadataName("Microsoft.Data.SqlClient.SqlDataReader")!;
                Debug.Assert(_sqlDataReaderType is not null, "type Microsoft.Data.SqlClient.SqlDataReader not found");

                var dbNullType = _compilation.GetTypeByMetadataName("System.DBNull")!;
                Debug.Assert(dbNullType is not null, "type System.DBNull not found");

                // Combined types
                _immutableArrayOfStringsType = _immutableArrayType
                    .Construct(_stringType);
                Debug.Assert(_immutableArrayOfStringsType is not null, "type ImmutableArray<string> not found");

                _immutableArrayOfObjectsType = _immutableArrayType
                    .Construct(_objectType);
                Debug.Assert(_immutableArrayOfObjectsType is not null, "type ImmutableArray<objects> not found");

                var immutableArrayBuilderType = _immutableArrayType.GetTypeMembers("Builder").Single();
                Debug.Assert(immutableArrayBuilderType is not null, "type ImmutableArray<T>.Builder not found");

                var staticImmutableArrayType = _compilation.GetTypeByMetadataName("System.Collections.Immutable.ImmutableArray")!;
                Debug.Assert(staticImmutableArrayType is not null, "type ImmutableArray class not found");

                // Methods
                _immutableArrayBuilderMethodSymbol = staticImmutableArrayType
                    .GetMembers("CreateBuilder")
                    .OfType<MethodSymbol>()
                    .Single(m => m.Parameters.Length == 0);
                Debug.Assert(_immutableArrayBuilderMethodSymbol is not null, "method ImmutableArray.CreateBuilder<T>() not found");

                _immutableArrayBuilderToImmutableArrayMethodSymbol = staticImmutableArrayType
                    .GetMembers("ToImmutableArray")
                    .OfType<MethodSymbol>()
                    .Single(
                        m => m.Parameters.Length == 1 &&
                        m.Parameters[0].Type.OriginalDefinition.Equals(immutableArrayBuilderType));
                Debug.Assert(_immutableArrayBuilderToImmutableArrayMethodSymbol is not null, "method ImmutableArray.Builder<T>.ToImmutableArray() not found");

                // Functions
                _beginMethodSymbol = TryLookupFunction(
                    "iWare.Database.SqlCommands",
                    "Begin")!;
                Debug.Assert(_beginMethodSymbol is not null, "method iWare.Database.SqlCommands.Begin not found");
                Debug.Assert(_beginMethodSymbol.Parameters.Length == 3 && !_beginMethodSymbol.ReturnsVoid, "method iWare.Database.SqlCommands.Begin does not match expected signature");

                _endMethodSymbol = TryLookupFunction(
                    "iWare.Database.SqlCommands",
                    "End")!;
                Debug.Assert(_endMethodSymbol is not null, "method iWare.Database.SqlCommands.End not found");
                Debug.Assert(_endMethodSymbol.Parameters.Length == 1 && _endMethodSymbol.ReturnsVoid, "method iWare.Database.SqlCommands.End does not match expected signature");

                _readMethodSymbol = TryLookupFunction(
                    "iWare.Database.SqlCommands",
                    "Read")!;
                Debug.Assert(_readMethodSymbol is not null, "method iWare.Database.SqlCommands.Read() not found");
                Debug.Assert(
                    _readMethodSymbol.Parameters.Length == 2 &&
                    _readMethodSymbol.Parameters[0].Type.Equals(_sqlDataReaderType) &&
                    _readMethodSymbol.Parameters[1].Type.Equals(_immutableArrayOfStringsType) &&
                    !_readMethodSymbol.ReturnsVoid,
                    "method iWare.Database.SqlCommands.Read() does not match expected signature");

                // Fields
                _dbNullValueProperty = dbNullType
                    .GetMembers("Value")
                    .OfType<FieldSymbol>()
                    .Single();
                Debug.Assert(_dbNullValueProperty is not null, "property System.DBNull.Value not found");
            }

            public BoundStatement RewriteSqlStatement()
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
                //     sqlReader = Begin(/*QUERY*/, parameters);
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
                //     End(sqlReader);
                //     if (sqlEndAction is not null)
                //     {
                //         /*ALWAYS RUN*/
                //     }
                // }

                var readMethodTargetType = _readMethodSymbol.ReturnType;
                var beginMethodTargetType = _beginMethodSymbol.ReturnType;
                var sideEffects = ImmutableArray.CreateBuilder<BoundStatement>();
                _arrayIsDefaultOrEmptyMethodSymbol = readMethodTargetType.GetMembers("get_IsDefaultOrEmpty").OfType<MethodSymbol>().Single();

                // SqlDataReader? sqlReader = null;
                var sqlReaderSymbol = _factory.SynthesizedLocal(beginMethodTargetType);
                var sqlReaderLocal = _factory.Local(sqlReaderSymbol);
                sideEffects.Add(
                    _factory.Assignment(
                        sqlReaderLocal,
                        _factory.Null(_sqlDataReaderType)));

                // ImmutableArray<object?>? readValues = null;
                var readValuesSymbol = _factory.SynthesizedLocal(readMethodTargetType);
                var readValuesLocal = _factory.Local(readValuesSymbol);

                // ImmutableArray<string> sqlNameValues = sqlQueryNames;
                var sqlNamesSymbol = _factory.SynthesizedLocal(_immutableArrayOfStringsType);
                var sqlNamesLocal = _factory.Local(sqlNamesSymbol);
                sideEffects.Add(AssignImmutableArrayToLocal(sqlNamesLocal, _querySqlNames));

                // ImmutableArray<object> parameters = parameterSymbols;
                var parametersSymbol = _factory.SynthesizedLocal(_immutableArrayOfObjectsType);
                var parametersLocal = _factory.Local(parametersSymbol);
                sideEffects.Add(AssignImmutableArrayToLocal(parametersLocal, _parameterSymbols));

                // ImmutableArray<string> parameterNames = parameterNames;
                var parameterNamesSymbol = _factory.SynthesizedLocal(_immutableArrayOfStringsType);
                var parameterNamesLocal = _factory.Local(parameterNamesSymbol);
                sideEffects.Add(AssignImmutableArrayToLocal(parameterNamesLocal, _parameterNames));

                // sqlReader = Begin(/*QUERY*/, parameters);
                var sqlReaderBeginStatement = _factory.Assignment(
                    sqlReaderLocal,
                    _factory.Call(
                        null,
                        _beginMethodSymbol,
                        ImmutableArray.Create<BoundExpression>(
                            _sqlTextBoundLiteral,
                            parametersLocal,
                            parameterNamesLocal)));

                //  /*ASSIGN LOCALS*/
                var assignLocalsStatements = AssignLocals(_querySymbols, readValuesLocal);

                // do
                // {
                //     /*ASSIGN LOCALS*/
                //     /*FOREACH*/
                // } while ((/*READ*/) != null);
                // or empty block if sqlDo is null
                BoundStatement sqlDoLoop = CreateSqlDoLoop(
                    assignLocalsStatements,
                    _sqlDoBoundBlock,
                    readValuesLocal,
                    sqlReaderLocal,
                    sqlNamesLocal);

                // /*EMPTY RESULT*/
                // or empty block if sqlEmpty is null
                BoundStatement sqlEmptyStatement;
                if (_sqlEmptyBoundBlock != null)
                {
                    sqlEmptyStatement = _factory.Block(_sqlEmptyBoundBlock);
                }
                else
                {
                    sqlEmptyStatement = _factory.Block();
                }

                // if (/*READ*/ != null) { ... } else { ... }
                var readCallStatement = CreateReadCall(readValuesLocal, sqlReaderLocal, sqlNamesLocal);
                var ifRead = _factory.If(
                    _factory.Not(_factory.Call(readValuesLocal, _arrayIsDefaultOrEmptyMethodSymbol)),
                    sqlDoLoop,
                    sqlEmptyStatement);

                // End(sqlReader);
                var sqlReaderEndStatement = _factory.ExpressionStatement(
                    _factory.Call(
                        null,
                        _endMethodSymbol,
                        ImmutableArray.Create<BoundExpression>(sqlReaderLocal)));

                // /*ALWAYS RUN*/
                // or empty block if sqlEnd is null
                BoundStatement sqlEndStatement;
                if (_sqlEndBoundBlock != null)
                {
                    sqlEndStatement = _factory.Block(_sqlEndBoundBlock);
                }
                else
                {
                    sqlEndStatement = _factory.Block();
                }

                // try { ... } finally { ... }
                var finallyBlock = _factory.Block(sqlReaderEndStatement, sqlEndStatement);
                var tryBlock = _factory.Block(sqlReaderBeginStatement, readCallStatement, ifRead);
                var finallyLabel = new GeneratedLabelSymbol("sqlFinally");
                sideEffects.Add(_factory.Try(tryBlock, [], finallyBlock, finallyLabel));

                var sideEffectsImmutable = sideEffects.ToImmutableArray();
                var ret = _factory.Block(
                    [
                        sqlReaderSymbol,
                        readValuesSymbol,
                        sqlNamesSymbol,
                        parametersSymbol,
                        parameterNamesSymbol
                    ],
                    sideEffectsImmutable);
                // ret.DumpSource is VERY useful!!!!
                return ret;
            }

            private BoundExpressionStatement CreateReadCall(BoundLocal readValuesLocal, BoundLocal sqlReaderLocal, BoundLocal sqlNamesLocal)
            {
                // /*READ*/ readValues = Read(sqlReader, nameValues)
                // ImmutableArray<object?>? Read(IDataReader reader, ImmutableArray<string> querySqlNames)
                return _factory.ExpressionStatement(_factory.AssignmentExpression(
                    readValuesLocal,
                    _factory.Call(
                        null,
                        _readMethodSymbol,
                        ImmutableArray.Create<BoundExpression>(
                            sqlReaderLocal,
                            sqlNamesLocal))));
            }

            private BoundBlock AssignImmutableArrayToLocal<T>(BoundLocal local, ImmutableArray<T> arr)
            {
                // T supports string and Symbol, anything else fails
                var sideEffects = ImmutableArray.CreateBuilder<BoundStatement>();

                var targetType = arr switch
                {
                    ImmutableArray<string> _ => _stringType,
                    ImmutableArray<Symbol> _ => _objectType,
                    _ => null
                };
                Debug.Assert(targetType is not null, "Type error in AssignImmutableArrayToLocal");
                var immutableArrayType = _immutableArrayType
                    .Construct(targetType);
                Debug.Assert(immutableArrayType is not null, $"type ImmutableArray<{targetType}> could not be constructed");
                var immutableArrayBuilderType = immutableArrayType
                    .GetTypeMembers("Builder")
                    .Single();
                Debug.Assert(immutableArrayBuilderType is not null, $"type ImmutableArray.Builder<{targetType}> not found");
                var addMethod = immutableArrayBuilderType
                    .GetMembers("Add")
                    .OfType<MethodSymbol>()
                    .Single(
                        m => m.Parameters.Length == 1 &&
                        m.Parameters[0].Type.Equals(targetType));
                Debug.Assert(addMethod is not null, $"method ImmutableArray.Builder<{targetType}>.Add() not found");
                var immutableArrayBuilderMethod = _immutableArrayBuilderMethodSymbol.Construct(targetType);
                Debug.Assert(immutableArrayBuilderMethod is not null, $"method ImmutableArray.Builder<{targetType}>.CreateBuilder() not found");
                var toImmutableArrayMethodSymbol = _immutableArrayBuilderToImmutableArrayMethodSymbol.Construct(targetType);
                Debug.Assert(toImmutableArrayMethodSymbol is not null, $"method ImmutableArray.Builder<{targetType}>.ToImmutableArray() not found");

                //var builder = ImmutableArray.Builder<T>();
                var builderSymbol = _factory.SynthesizedLocal(immutableArrayBuilderType);
                var builderLocal = _factory.Local(builderSymbol);
                sideEffects.Add(
                    _factory.Assignment(
                        builderLocal,
                        _factory.Call(
                            null,
                            immutableArrayBuilderMethod)));

                //foreach o in arr builder.Add(o)
                foreach (var o in arr)
                {
                    var expr = o switch
                    {
                        string str => _factory.Literal(str),
                        LocalSymbol sym => _factory.Convert(_objectType, _factory.Local(sym)),
                        _ => null
                    };
                    if (expr is null)
                        Debug.Fail("Type error in AssignImmutableArrayToLocal");

                    sideEffects.Add(
                        _factory.ExpressionStatement(_factory.Call(
                            builderLocal,
                            addMethod,
                            expr)));
                }

                //sqlNamesLocal = builder.ToImmutableArray();
                sideEffects.Add(_factory.Assignment(
                    local,
                    _factory.Call(
                        null,
                        toImmutableArrayMethodSymbol,
                        builderLocal)));

                return _factory.Block(
                    [builderSymbol],
                    sideEffects.ToImmutableArray());
            }

            private BoundStatement CreateSqlDoLoop(BoundStatement assignLocalsStatements, BoundBlock? sqlDoBoundBlock, BoundLocal readValuesLocal, BoundLocal sqlReaderLocal, BoundLocal sqlNamesLocal)
            {
                if (sqlDoBoundBlock != null)
                {
                    var readCallStatement = CreateReadCall(readValuesLocal, sqlReaderLocal, sqlNamesLocal);
                    var startLabel = new GeneratedLabelSymbol("sqlDoStart");
                    var conditionalGoto = _factory.ConditionalGoto(
                        _factory.Not(_factory.Call(readValuesLocal, _arrayIsDefaultOrEmptyMethodSymbol!)),
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
                //  for (int i .. querySymbols.Length)
                //  {
                //      var symbol = querySymbols[i];
                //      var tmp = readValues[i];
                //      if (tmp != null)
                //      {
                //          querySymbols[i] = tmp;
                //      }
                //  }
                var sideEffects = ImmutableArray.CreateBuilder<BoundStatement>();
                var tmpSymbol = _factory.SynthesizedLocal(_objectType);
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
                        _factory.Convert(
                            tmpLocal.Type,
                            _factory.Call(
                                readValuesLocal,
                                getItemMethodSymbol,
                                [_factory.Literal(i)]),
                            Conversion.Boxing));

                    // if tmpLocal == DBNull.Value then tmpLocal = null
                    var objConvertStatement = _factory.If(
                        _factory.ObjectEqual(
                            tmpLocal,
                            _factory.Field(null, _dbNullValueProperty)),
                        _factory.Assignment(tmpLocal, _factory.Null(tmpLocal.Type)));
                    var conversion = _factory.Convert(
                        targetType,
                        tmpLocal,
                        Conversion.Unboxing);

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
                        objConvertStatement,
                        _factory.Assignment(lhs, conversion));
                }
                return _factory.Block([tmpSymbol], sideEffects.ToImmutableArray());
            }

            private MethodSymbol? TryLookupFunction(string @namespace, string functionName)
            {
                var type = _compilation.GetTypeByMetadataName(@namespace);
                var myFunction = type?
                    .GetMembers(functionName)
                    .OfType<MethodSymbol>()
                    .FirstOrDefault();
                return myFunction;
            }
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
    }
}
