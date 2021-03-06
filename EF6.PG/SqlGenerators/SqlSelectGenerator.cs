﻿using System;
using System.Linq;
using System.Data.Common;
using System.Diagnostics;
using System.Data.Entity.Core.Common.CommandTrees;
using System.Data.Entity.Core.Metadata.Edm;

namespace Npgsql.SqlGenerators
{
    internal class SqlSelectGenerator : SqlBaseGenerator
    {
        readonly DbQueryCommandTree _commandTree;

        public SqlSelectGenerator(DbQueryCommandTree commandTree)
        {
            _commandTree = commandTree;
        }

        protected SqlSelectGenerator()
        {
            // used only for other generators such as returning
        }

        public override VisitedExpression Visit(DbPropertyExpression expression)
        {
            /*
             * Algorithm for finding the correct reference expression: "Collection"."Name"
             * The collection is always a leaf InputExpression, found by lookup in _refToNode.
             * The name for the collection is found using node.TopName.
             *
             * We must now follow the path from the leaf down to the root,
             * and make sure the column is projected all the way down.
             *
             * We need not project columns at a current InputExpression.
             * For example, in
             *  SELECT ? FROM <from> AS "X" WHERE "X"."field" = <value>
             * we use the property "field" but it should not be projected.
             * Current expressions are stored in _currentExpressions.
             * There can be many of these, for example if we are in a WHERE EXISTS (SELECT ...) or in the right hand side of an Apply expression.
             *
             * At join nodes, column names might have to be renamed, if a name collision occurs.
             * For example, the following would be illegal,
             *  SELECT "X"."A" AS "A", "Y"."A" AS "A" FROM (SELECT 1 AS "A") AS "X" CROSS JOIN (SELECT 1 AS "A") AS "Y"
             * so we write
             *  SELECT "X"."A" AS "A", "Y"."A" AS "A_Alias<N>" FROM (SELECT 1 AS "A") AS "X" CROSS JOIN (SELECT 1 AS "A") AS "Y"
             * The new name is then propagated down to the root.
             */

            var name = expression.Property.Name;
            var from = expression.Instance.ExpressionKind == DbExpressionKind.Property
                ? ((DbPropertyExpression)expression.Instance).Property.Name
                : ((DbVariableReferenceExpression)expression.Instance).VariableName;

            var node = RefToNode[from];
            from = node.TopName;
            while (node != null)
            {
                foreach (var item in node.Selects)
                {
                    if (CurrentExpressions.Contains(item.Exp))
                        continue;

                    var use = new StringPair(from, name);

                    if (!item.Exp.ColumnsToProject.ContainsKey(use))
                    {
                        var oldName = name;
                        while (item.Exp.ProjectNewNames.Contains(name))
                            name = oldName + "_" + NextAlias();
                        item.Exp.ColumnsToProject[use] = name;
                        item.Exp.ProjectNewNames.Add(name);
                    }
                    else
                        name = item.Exp.ColumnsToProject[use];
                    from = item.AsName;
                }
                node = node.JoinParent;
            }
            return new ColumnReferenceExpression { Variable = from, Name = name };
        }

        // must provide a NULL of the correct type
        // this is necessary for certain types of union queries.
        public override VisitedExpression Visit(DbNullExpression expression)
            => new CastExpression(new LiteralExpression("NULL"), GetDbType(expression.ResultType.EdmType));

        public override void BuildCommand(DbCommand command)
        {
            Debug.Assert(command is NpgsqlCommand);
            Debug.Assert(_commandTree.Query is DbProjectExpression);
            var ve = _commandTree.Query.Accept(this);
            Debug.Assert(ve is InputExpression);
            var pe = (InputExpression)ve;
            command.CommandText = pe.ToString();

            // We retrieve all strings as unknowns in text format in the case the data types aren't really texts
            ((NpgsqlCommand)command).UnknownResultTypeList = pe.Projection.Arguments.Select(a => ((PrimitiveType)((ColumnExpression)a).ColumnType.EdmType).PrimitiveTypeKind == PrimitiveTypeKind.String).ToArray();

            // We must treat sbyte and DateTimeOffset specially so the value is read correctly
            if (pe.Projection.Arguments.Any(a => {
                var kind = ((PrimitiveType)((ColumnExpression)a).ColumnType.EdmType).PrimitiveTypeKind;
                return kind == PrimitiveTypeKind.SByte || kind == PrimitiveTypeKind.DateTimeOffset;
            }))
            {
                ((NpgsqlCommand)command).ObjectResultTypes = pe.Projection.Arguments.Select(a =>
                {
                    var kind = ((PrimitiveType)((ColumnExpression)a).ColumnType.EdmType).PrimitiveTypeKind;
                    switch (kind)
                    {
                    case PrimitiveTypeKind.SByte:
                        return typeof(sbyte);
                    case PrimitiveTypeKind.DateTimeOffset:
                        return typeof(DateTimeOffset);
                    default:
                        return null;
                    }
                }).ToArray();
            }
        }
    }
}
