using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Microsoft.CodeAnalysis.CSharp.iWareSql
{
    internal class Resolver
    {
        public static string AddTenantIdToTableNames(string sql, string tenantId)
        {
            // Use whichever parser version matches your target SQL Server version
            TSqlParser parser = new TSql160Parser(initialQuotedIdentifiers: true);

            IList<ParseError> errors;
            TSqlFragment fragment;

            using (var reader = new System.IO.StringReader(sql))
            {
                fragment = parser.Parse(reader, out errors);
            }

            if (errors != null && errors.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (var e in errors)
                    sb.AppendLine($"Line {e.Line}: {e.Message}");
                throw new InvalidOperationException("SQL parse errors:\n" + sb);
            }

            var visitor = new TableNameCollectorVisitor();
            fragment.Accept(visitor);

            // Get the original token stream so we can do a precise, formatting-preserving rewrite
            var tokens = fragment.ScriptTokenStream;

            // Build a set of token index ranges to replace (start/end token index of each table's Identifier(s))
            // We replace the *last* identifier in a multi-part name (schema.table -> schema.table_00001 style
            // would be wrong for "every instance of a table" semantics, so instead we replace the WHOLE
            // multi-part name with just the single new identifier).
            var edits = new List<(int startTokenIndex, int endTokenIndex, string newText)>();

            foreach (var namedTable in visitor.TableReferences)
            {
                var id = namedTable.SchemaObject; // SchemaObjectName
                if (id == null) continue;

                // Collect all Identifier parts that make up the multi-part name (server.database.schema.table)
                if (id.BaseIdentifier == null)
                {
                    continue;
                }
                Identifier identifier = id.BaseIdentifier;
                int startIdx = identifier.FirstTokenIndex;
                int endIdx = identifier.LastTokenIndex;
                var replacementName = $"{identifier.Value}_{tenantId}";

                edits.Add((startIdx, endIdx, replacementName));
            }

            return ApplyEdits(tokens, edits);
        }

        private static string ApplyEdits(IList<TSqlParserToken> tokens, List<(int startTokenIndex, int endTokenIndex, string newText)> edits)
        {
            // Sort edits by start index so we can skip replaced ranges while walking tokens
            edits.Sort((a, b) => a.startTokenIndex.CompareTo(b.startTokenIndex));

            var sb = new StringBuilder();
            int i = 0;
            int editPtr = 0;

            while (i < tokens.Count)
            {
                if (editPtr < edits.Count && edits[editPtr].startTokenIndex == i)
                {
                    sb.Append(edits[editPtr].newText);
                    i = edits[editPtr].endTokenIndex + 1; // skip past the replaced tokens
                    editPtr++;
                }
                else
                {
                    sb.Append(tokens[i].Text);
                    i++;
                }
            }

            return sb.ToString();
        }
    }

#pragma warning disable RS0016
    /// <summary>
    /// Walks the AST and collects every NamedTableReference (covers FROM, JOIN,
    /// UPDATE target, INSERT INTO target, DELETE FROM target, MERGE INTO, etc.)
    /// </summary>
    public class TableNameCollectorVisitor : TSqlFragmentVisitor
    {
        public List<NamedTableReference> TableReferences { get; } = new List<NamedTableReference>();

        public override void ExplicitVisit(NamedTableReference node)
        {
            TableReferences.Add(node);
            base.ExplicitVisit(node);
        }
    }
#pragma warning restore RS0016
}
