// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using Roslyn.Utilities;
using System.Linq;

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

            // Types
            var iDataRecordType = _compilation.GetTypeByMetadataName("System.Data.IDataRecord");
            Debug.Assert(iDataRecordType is not null, "Could not find type System.Data.IDataRecord");
            var sqlDoType = _compilation.GetWellKnownType(WellKnownType.System_Action_T2).Construct(
                    iDataRecordType,
                    _compilation.GetSpecialType(SpecialType.System_Boolean));

            // sqlDo
            BoundExpression sqlDoBoundLiteral = _factory.Null(sqlDoType);
            //if (node.SqlDoOpt is not null)
            //{
            //    Debug.Assert(node.SqlDoOpt.Kind != BoundKind.UnboundLambda, "Can't be an unbound lambda");
            //    sqlDoBoundLiteral = (BoundExpression)Visit(node.SqlDoOpt)!;
            //}

            var sqlEmptyType = _compilation.GetWellKnownType(WellKnownType.System_Action);
            var sqlEmptyBoundLiteral = _factory.Null(sqlEmptyType);
            var sqlEndBoundLiteral = _factory.Null(sqlEmptyType);

            var mySqlMethod = tryLookupMySqlFunction(node.Syntax);
            Debug.Assert(mySqlMethod is not null, "iWare.Database.SqlCommands.SqlCommand is missing");

            var boundCall = _factory.Call(
                receiver: null,
                method: mySqlMethod,
                args: ImmutableArray.Create(sqlTextBoundLiteral, sqlDoBoundLiteral, sqlEmptyBoundLiteral, sqlEndBoundLiteral)
                );

            return new BoundExpressionStatement(node.Syntax, boundCall);

            MethodSymbol? tryLookupMySqlFunction(SyntaxNode syntax)
            {
                var type = _compilation.GetTypeByMetadataName("iWare.Database.SqlCommands");
                var mySqlFunction = type?
                    .GetMembers("SqlCommand")
                    .OfType<MethodSymbol>()
                    .FirstOrDefault(m => m.Parameters.Length == 4 && m.Parameters[0].Type.SpecialType == SpecialType.System_String);
                return mySqlFunction;
            }
        }

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
