// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.iWareSql;

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
            private readonly ImmutableArray<BoundExpression> _queryTargets;
            private readonly ImmutableArray<string> _querySqlNames;
            private readonly ImmutableArray<BoundExpression> _entityBuildTargets;
            private readonly ImmutableArray<int> _entityBuildFirstColumns;
            private readonly MethodSymbol? _toRowVersionMethodSymbol;
            private readonly ImmutableArray<BoundExpression> _parameterExpressions;
            private readonly ImmutableArray<string> _parameterNames;
            // Fields
            private readonly FieldSymbol _dbNullValueProperty;

            private readonly LocalRewriter _localRewriter;

            public RewriteSql(CSharpCompilation compilation, SyntheticBoundNodeFactory factory, BoundSqlStatement node, LocalRewriter localRewriter)
            {
                _compilation = compilation;
                _factory = factory;
                _localRewriter = localRewriter;

                _sqlTextBoundLiteral = _factory.Literal(node.SqlContents);

                _sqlDoBoundBlock = null;
                if (node.SqlDoOpt is not null)
                {
                    _sqlDoBoundBlock = (BoundBlock)localRewriter.VisitBlock(node.SqlDoOpt);
                }
                _sqlEmptyBoundBlock = null;
                if (node.SqlEmptyOpt is not null)
                {
                    _sqlEmptyBoundBlock = (BoundBlock)localRewriter.VisitBlock(node.SqlEmptyOpt);
                }
                _sqlEndBoundBlock = null;
                if (node.SqlEndOpt is not null)
                {
                    _sqlEndBoundBlock = (BoundBlock)localRewriter.VisitBlock(node.SqlEndOpt);
                }

                _queryTargets = node.QueryTargets;
                _querySqlNames = node.QuerySqlNames;
                _entityBuildTargets = node.EntityBuildTargets;
                _entityBuildFirstColumns = node.EntityBuildFirstColumns;
                _parameterExpressions = node.ParameterExpressions;
                _parameterNames = node.ParameterNames;

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
                if (_beginMethodSymbol is null || _beginMethodSymbol.Parameters.Length != 3 || _beginMethodSymbol.ReturnsVoid)
                {
                    throw new Exception("Missing or invalid method iWare.Database.SqlCommands.Begin");
                }
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

                // Only needed by a star select into an [Orm] entity, so looked up leniently:
                // a project with no such select never touches it.
                _toRowVersionMethodSymbol = TryLookupFunction("iWare.Database.SqlCommands", "ToRowVersion");

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
                sideEffects.Add(AssignStringArrayToLocal(sqlNamesLocal, _querySqlNames));

                // ImmutableArray<object> parameters = /*INPUT EXPRESSIONS*/;
                var parametersSymbol = _factory.SynthesizedLocal(_immutableArrayOfObjectsType);
                var parametersLocal = _factory.Local(parametersSymbol);
                sideEffects.Add(AssignExpressionArrayToLocal(parametersLocal, _parameterExpressions));

                // ImmutableArray<string> parameterNames = parameterNames;
                var parameterNamesSymbol = _factory.SynthesizedLocal(_immutableArrayOfStringsType);
                var parameterNamesLocal = _factory.Local(parameterNamesSymbol);
                sideEffects.Add(AssignStringArrayToLocal(parameterNamesLocal, _parameterNames));

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
                var assignLocalsStatements = AssignLocals(_queryTargets, readValuesLocal);

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

            private BoundBlock AssignStringArrayToLocal(BoundLocal local, ImmutableArray<string> values)
            {
                var elements = ImmutableArray.CreateBuilder<BoundExpression>(values.Length);
                foreach (var value in values)
                {
                    elements.Add(_factory.Literal(value));
                }

                return BuildImmutableArray(local, _stringType, elements.ToImmutable());
            }

            private BoundBlock AssignExpressionArrayToLocal(BoundLocal local, ImmutableArray<BoundExpression> expressions)
            {
                var elements = ImmutableArray.CreateBuilder<BoundExpression>(expressions.Length);
                foreach (var expression in expressions)
                {
                    elements.Add(ConvertToObject(expression));
                }

                return BuildImmutableArray(local, _objectType, elements.ToImmutable());
            }

            /// <summary>
            /// Lowers an input expression and converts it to <c>object</c> so it can be stored in the
            /// parameter array. Boxing for value types, an implicit reference conversion otherwise.
            /// </summary>
            private BoundExpression ConvertToObject(BoundExpression expression)
            {
                // The expression comes straight from the binder, so it has to be lowered before it
                // is embedded in the synthesized tree — a property read has to become its getter
                // call here, an indexer read its get_Item call, and so on.
                var lowered = _localRewriter.VisitExpression(expression)!;

                // A null type means binding already failed and reported; the statement carries
                // HasErrors and will not reach codegen, so just hand the expression back untouched
                // rather than crashing on the way there.
                if (lowered.Type is null || lowered.Type.Equals(_objectType, TypeCompareKind.AllIgnoreOptions))
                {
                    return lowered;
                }

                var useSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
                var conversion = _compilation.Conversions.ClassifyConversionFromType(
                    lowered.Type, _objectType, isChecked: false, ref useSiteInfo);
                Debug.Assert(conversion.Exists, $"No conversion found from {lowered.Type} to object");

                return _localRewriter.MakeConversionNode(
                    syntax: expression.Syntax,
                    rewrittenOperand: lowered,
                    conversion: conversion,
                    rewrittenType: _objectType,
                    @checked: false);
            }

            private BoundBlock BuildImmutableArray(BoundLocal local, TypeSymbol targetType, ImmutableArray<BoundExpression> elements)
            {
                var sideEffects = ImmutableArray.CreateBuilder<BoundStatement>();

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

                //foreach element builder.Add(element)
                foreach (var element in elements)
                {
                    sideEffects.Add(
                        _factory.ExpressionStatement(_factory.Call(
                            builderLocal,
                            addMethod,
                            element)));
                }

                //local = builder.ToImmutableArray();
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

            BoundStatement AssignLocals(ImmutableArray<BoundExpression> queryTargets, BoundExpression readValuesLocal)
            {
                var sideEffects = ImmutableArray.CreateBuilder<BoundStatement>();
                var locals = ImmutableArray.CreateBuilder<LocalSymbol>();
                var tmpSymbol = _factory.SynthesizedLocal(_objectType);
                var tmpLocal = _factory.Local(tmpSymbol);
                locals.Add(tmpSymbol);
                var getItemMethodSymbol =
                    readValuesLocal.Type!
                        .GetMembers("get_Item")
                        .OfType<MethodSymbol>()
                        .FirstOrDefault();
                Debug.Assert(getItemMethodSymbol is not null, "ImmutableArray<object>.get_Item() not found");
                Debug.Assert(getItemMethodSymbol.Parameters.Length == 1, "ImmutableArray<object>.get_Item() not found");

                // Open generic Domain<T> — used to detect "this target type wraps some underlying T".
                var domainOfTOpenType = _compilation.GetTypeByMetadataName("iWareSql.Domain.Abstractions.Domain`1");
                Debug.Assert(domainOfTOpenType is not null, "type iWareSql.Domain.Abstractions.Domain<T> not found");

                for (int i = 0; i < queryTargets.Length; i++)
                {
                    var target = queryTargets[i];
                    var targetType = target.Type;
                    if (targetType is null)
                    {
                        // Binding already failed and reported for this target; the statement carries
                        // HasErrors and will not reach codegen. Skip it rather than crash first.
                        continue;
                    }

                    var assignTmp = _factory.Assignment(
                        tmpLocal,
                        _factory.Convert(
                            tmpLocal.Type,
                            _factory.Call(readValuesLocal, getItemMethodSymbol, [_factory.Literal(i)]),
                            Conversion.Boxing));

                    var objConvertStatement = _factory.If(
                        _factory.ObjectEqual(tmpLocal, _factory.Field(null, _dbNullValueProperty)),
                        _factory.Assignment(tmpLocal, _factory.Null(tmpLocal.Type)));

                    // Land the converted value in a temp of the target's own type. Doing this before
                    // the real assignment keeps the right-hand side a plain local read, which is safe
                    // for VisitAssignmentOperator to walk over again.
                    var valueSymbol = _factory.SynthesizedLocal(targetType);
                    var valueLocal = _factory.Local(valueSymbol);
                    locals.Add(valueSymbol);

                    sideEffects.AddRange(
                        assignTmp,
                        objConvertStatement,
                        _factory.Assignment(
                            valueLocal,
                            BuildConversionFromObject(tmpLocal, targetType, domainOfTOpenType)),
                        _factory.ExpressionStatement(AssignToTarget(target, valueLocal)));
                }

                for (int k = 0; k < _entityBuildTargets.Length; k++)
                {
                    BuildEntity(
                        _entityBuildTargets[k],
                        _entityBuildFirstColumns[k],
                        readValuesLocal,
                        getItemMethodSymbol!,
                        domainOfTOpenType!,
                        tmpLocal,
                        locals,
                        sideEffects);
                }

                return _factory.Block(locals.ToImmutable(), sideEffects.ToImmutableArray());
            }

            /// <summary>
            /// Emits <c>target = new T_Builder().AddCol(v0)....AddRowVersion(rv).Build();</c> for
            /// one star select into an [Orm] entity.
            /// </summary>
            /// <remarks>
            /// The entity's columns are partial properties standing in front of a values object
            /// the constructor supplies, so there is nothing to assign into until the object
            /// exists. Constructing it here rather than filling it in also means the target only
            /// has to be assignable, not already assigned, and it is the only way the row version
            /// gets in - OrmTable keeps it outside the row's values and settable only from the
            /// constructor.
            ///
            /// The column order is <see cref="OrmSchemaProvider.GetColumns"/>'s, which is the same
            /// order the binder laid the result columns out in.
            /// </remarks>
            private void BuildEntity(
                BoundExpression target,
                int firstColumn,
                BoundExpression readValuesLocal,
                MethodSymbol getItemMethodSymbol,
                NamedTypeSymbol domainOfTOpenType,
                BoundExpression tmpLocal,
                ImmutableArray<LocalSymbol>.Builder locals,
                ImmutableArray<BoundStatement>.Builder sideEffects)
            {
                if (target.Type is not NamedTypeSymbol entityType ||
                    !Binder.TryGetOrmBuilder(entityType, out var builderType))
                {
                    // Binding would have reported this; the statement carries HasErrors and will
                    // not reach codegen.
                    return;
                }

                var builderCtor = builderType.InstanceConstructors.FirstOrDefault(static c => c.ParameterCount == 0);
                var buildMethod = builderType.GetMembers("Build").OfType<MethodSymbol>().FirstOrDefault(static m => m.ParameterCount == 0);
                if (builderCtor is null || buildMethod is null)
                {
                    return;
                }

                BoundExpression chain = _factory.New(builderCtor);
                var column = firstColumn;

                foreach (var ormColumn in OrmSchemaProvider.GetColumns(entityType.GetPublicSymbol()))
                {
                    if (FindAddMethod(builderType, "Add" + ormColumn.PropertyName) is not { } addMethod)
                    {
                        return;
                    }

                    chain = _factory.Call(
                        chain,
                        addMethod,
                        ReadColumnInto(addMethod.Parameters[0].Type, column++, readValuesLocal, getItemMethodSymbol, domainOfTOpenType, tmpLocal, locals, sideEffects));
                }

                if (FindAddMethod(builderType, "AddRowVersion") is not { } addRowVersion || _toRowVersionMethodSymbol is null)
                {
                    return;
                }

                // The row version comes over as bytes rather than a number, so it goes through a
                // helper instead of the ordinary unboxing conversion the other columns use.
                var rowVersionSymbol = _factory.SynthesizedLocal(addRowVersion.Parameters[0].Type);
                var rowVersionLocal = _factory.Local(rowVersionSymbol);
                locals.Add(rowVersionSymbol);
                sideEffects.Add(
                    _factory.Assignment(
                        rowVersionLocal,
                        _factory.Call(
                            null,
                            _toRowVersionMethodSymbol,
                            [ReadColumnAsObject(column, readValuesLocal, getItemMethodSymbol)])));

                chain = _factory.Call(chain, addRowVersion, rowVersionLocal);

                var entitySymbol = _factory.SynthesizedLocal(entityType);
                var entityLocal = _factory.Local(entitySymbol);
                locals.Add(entitySymbol);

                sideEffects.Add(_factory.Assignment(entityLocal, _factory.Call(chain, buildMethod)));
                sideEffects.Add(_factory.ExpressionStatement(AssignToTarget(target, entityLocal)));
            }

            private static MethodSymbol? FindAddMethod(NamedTypeSymbol builderType, string name) =>
                builderType.GetMembers(name).OfType<MethodSymbol>().FirstOrDefault(static m => m.ParameterCount == 1);

            private BoundExpression ReadColumnAsObject(int column, BoundExpression readValuesLocal, MethodSymbol getItemMethodSymbol) =>
                _factory.Convert(
                    _objectType,
                    _factory.Call(readValuesLocal, getItemMethodSymbol, [_factory.Literal(column)]),
                    Conversion.Boxing);

            /// <summary>
            /// Reads one result column into a fresh local of <paramref name="valueType"/>, mapping
            /// DBNull to null on the way, and returns a read of that local.
            /// </summary>
            private BoundExpression ReadColumnInto(
                TypeSymbol valueType,
                int column,
                BoundExpression readValuesLocal,
                MethodSymbol getItemMethodSymbol,
                NamedTypeSymbol domainOfTOpenType,
                BoundExpression tmpLocal,
                ImmutableArray<LocalSymbol>.Builder locals,
                ImmutableArray<BoundStatement>.Builder sideEffects)
            {
                var valueSymbol = _factory.SynthesizedLocal(valueType);
                var valueLocal = _factory.Local(valueSymbol);
                locals.Add(valueSymbol);

                sideEffects.Add(
                    _factory.Assignment(
                        tmpLocal,
                        ReadColumnAsObject(column, readValuesLocal, getItemMethodSymbol)));
                sideEffects.Add(
                    _factory.If(
                        _factory.ObjectEqual(tmpLocal, _factory.Field(null, _dbNullValueProperty)),
                        _factory.Assignment(tmpLocal, _factory.Null(tmpLocal.Type))));
                sideEffects.Add(
                    _factory.Assignment(
                        valueLocal,
                        BuildConversionFromObject(tmpLocal, valueType, domainOfTOpenType)));

                return valueLocal;
            }

            /// <summary>
            /// Builds <c>target = value</c> in lowered form.
            /// </summary>
            /// <remarks>
            /// This goes through the local rewriter's own assignment lowering rather than
            /// <c>SyntheticBoundNodeFactory.Assignment</c>, which produces a raw
            /// <see cref="BoundAssignmentOperator"/> with no lowering at all. The rewriter visits the
            /// target with <c>isLeftOfAssignment: true</c>, so a property or indexer target becomes a
            /// call to its set accessor instead of its get accessor, and it handles implicit indexers
            /// (<c>^1</c> and ranges) and dynamic targets too.
            ///
            /// Any receiver is evaluated here, i.e. once per result row, which is what the equivalent
            /// hand-written assignment inside the read loop would do.
            /// </remarks>
            private BoundExpression AssignToTarget(BoundExpression target, BoundExpression value)
            {
                var assignment = new BoundAssignmentOperator(
                    target.Syntax,
                    target,
                    value,
                    isRef: false,
                    target.Type!)
                { WasCompilerGenerated = true };

                return _localRewriter.VisitAssignmentOperator(assignment, used: false);
            }

            private BoundExpression BuildConversionFromObject(
                BoundExpression objExpr,
                TypeSymbol targetType,
                NamedTypeSymbol domainOfTOpenType)
            {
                var useSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;

                if (TryFindDomainUnderlyingType(targetType, domainOfTOpenType, out var underlyingType))
                {
                    // object -> T (the reader's real runtime type). Always a primitive
                    // conversion (unboxing / explicit reference) — safe for codegen as-is.
                    var toUnderlying = _compilation.Conversions.ClassifyConversionFromType(
                        _objectType, underlyingType, isChecked: false, ref useSiteInfo);
                    var underlyingExpr = _factory.Convert(underlyingType, objExpr, toUnderlying);

                    // T -> targetType. This may be ImplicitUserDefined (e.g. implicit operator
                    // TSelf(string)) — MUST go through MakeConversionNode, not _factory.Convert,
                    // so the user-defined operator call actually gets lowered/emitted.
                    var toTarget = _compilation.Conversions.ClassifyConversionFromType(
                        underlyingType, targetType, isChecked: false, ref useSiteInfo);
                    Debug.Assert(toTarget.Exists, $"No conversion found from {underlyingType} to {targetType}");

                    return _localRewriter.MakeConversionNode(
                        syntax: _factory.Syntax,
                        rewrittenOperand: underlyingExpr,
                        conversion: toTarget,
                        rewrittenType: targetType,
                        @checked: false);
                }

                // Non-domain target: preserve prior behavior.
                return _factory.Convert(targetType, objExpr, Conversion.Unboxing);
            }

            private static bool TryFindDomainUnderlyingType(TypeSymbol targetType, NamedTypeSymbol domainOfTOpenType, out TypeSymbol underlyingType)
            {
                for (var t = targetType as NamedTypeSymbol; t is not null; t = t.BaseTypeNoUseSiteDiagnostics)
                {
                    if (t.OriginalDefinition.Equals(domainOfTOpenType, TypeCompareKind.ConsiderEverything))
                    {
                        underlyingType = t.TypeArgumentsWithAnnotationsNoUseSiteDiagnostics[0].Type;
                        return true;
                    }
                }
                underlyingType = null!;
                return false;
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
