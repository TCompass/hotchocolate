using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using HotChocolate.Execution;
using HotChocolate.Language;
using HotChocolate.Resolvers;
using HotChocolate.Stitching.Delegation;
using HotChocolate.Stitching.Properties;
using HotChocolate.Stitching.Utilities;
using HotChocolate.Types;
using HotChocolate.Utilities;

namespace HotChocolate.Stitching
{
    public class DelegateToRemoteSchemaMiddleware
    {
        private const string _remoteErrorField = "remote";
        private const string _schemaNameErrorField = "schemaName";
        private static readonly RootScopedVariableResolver _resolvers =
            new RootScopedVariableResolver();
        private readonly FieldDelegate _next;

        public DelegateToRemoteSchemaMiddleware(FieldDelegate next)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
        }

        public async Task InvokeAsync(IMiddlewareContext context)
        {
            if (context.Field.Directives.Contains(DirectiveNames.Delegate))
            {
                NameString schemaName = default;
                IReadOnlyCollection<IError>? errors = null;

                foreach (DelegateDirective delegateDirective in context.Field
                    .Directives[DirectiveNames.Delegate]
                    .Select(t => t.ToObject<DelegateDirective>()))
                {
                    schemaName = delegateDirective.Schema;

                    IImmutableStack<SelectionPathComponent> path =
                        delegateDirective.Path is null
                        ? ImmutableStack<SelectionPathComponent>.Empty
                        : SelectionPathParser.Parse(delegateDirective.Path);

                    IReadOnlyQueryRequest request = CreateQuery(context, schemaName, path);

                    IReadOnlyQueryResult result = await ExecuteQueryAsync(
                        context, request, schemaName)
                        .ConfigureAwait(false);

                    UpdateContextData(context, result, delegateDirective);

                    object data = ExtractData(result.Data, path.Count());
                    errors = result.Errors;

                    if (data is { })
                    {
                        context.Result = new SerializedData(data);
                        break;
                    }
                }

                if (errors is { } && errors.Count > 0)
                {
                    ReportErrors(schemaName, context, errors);
                }
            }

            await _next.Invoke(context).ConfigureAwait(false);
        }

        private void UpdateContextData(
            IResolverContext context,
            IReadOnlyQueryResult result,
            DelegateDirective delegateDirective)
        {
            if (result.ContextData.Count > 0)
            {
                ImmutableDictionary<string, object>.Builder builder =
                    ImmutableDictionary.CreateBuilder<string, object>();
                builder.AddRange(context.ScopedContextData);
                builder[WellKnownProperties.SchemaName] = delegateDirective.Schema;
                builder.AddRange(result.ContextData);
                context.ScopedContextData = builder.ToImmutableDictionary();
            }
            else
            {
                context.ModifyScopedContext(c => c.SetItem(
                    WellKnownProperties.SchemaName,
                    delegateDirective.Schema));
            }
        }

        private static IReadOnlyQueryRequest CreateQuery(
            IMiddlewareContext context,
            NameString schemaName,
            IImmutableStack<SelectionPathComponent> path)
        {
            var fieldRewriter = new ExtractFieldQuerySyntaxRewriter(
                context.Schema,
                context.Service<IEnumerable<IQueryDelegationRewriter>>());

            OperationType operationType =
                context.Schema.IsRootType(context.ObjectType)
                    ? context.Operation.Operation
                    : OperationType.Query;

            ExtractedField extractedField = fieldRewriter.ExtractField(
                schemaName, context.Document, context.Operation,
                context.FieldSelection, context.ObjectType);

            IEnumerable<Delegation.VariableValue> scopedVariables =
                ResolveScopedVariables(
                    context, schemaName, operationType,
                    path, fieldRewriter);

            IReadOnlyCollection<Delegation.VariableValue> variableValues =
                CreateVariableValues(
                    context, schemaName, scopedVariables,
                    extractedField, fieldRewriter);

            DocumentNode query = RemoteQueryBuilder.New()
                .SetOperation(context.Operation.Name, operationType)
                .SetSelectionPath(path)
                .SetRequestField(extractedField.Field)
                .AddVariables(CreateVariableDefs(variableValues))
                .AddFragmentDefinitions(extractedField.Fragments)
                .Build();

            var requestBuilder = QueryRequestBuilder.New();

            AddVariables(requestBuilder, query, variableValues);

            requestBuilder.SetQuery(query);
            requestBuilder.SetProperties(context.ContextData);
            requestBuilder.SetProperty(WellKnownProperties.IsAutoGenerated, true);

            return requestBuilder.Create();
        }

        private static async Task<IReadOnlyQueryResult> ExecuteQueryAsync(
            IResolverContext context,
            IReadOnlyQueryRequest request,
            string schemaName)
        {
            IRemoteQueryClient remoteQueryClient =
                context.Service<IStitchingContext>()
                    .GetRemoteQueryClient(schemaName);

            IExecutionResult result = await remoteQueryClient
                .ExecuteAsync(request)
                .ConfigureAwait(false);

            if (result is IReadOnlyQueryResult queryResult)
            {
                return queryResult;
            }

            throw new QueryException(
                StitchingResources.DelegationMiddleware_OnlyQueryResults);
        }

        private static object ExtractData(
            IReadOnlyDictionary<string, object> data,
            int levels)
        {
            if (data.Count == 0)
            {
                return null;
            }

            object obj = data.Count == 0 ? null : data.First().Value;

            if (obj != null && levels > 1)
            {
                for (int i = levels - 1; i >= 1; i--)
                {
                    var current = obj as IReadOnlyDictionary<string, object>;
                    obj = current.Count == 0 ? null : current.First().Value;
                    if (obj is null)
                    {
                        return null;
                    }
                }
            }

            return obj;
        }

        private static void ReportErrors(
            NameString schemaName,
            IResolverContext context,
            IEnumerable<IError> errors)
        {
            foreach (IError error in errors)
            {
                IErrorBuilder builder = ErrorBuilder.FromError(error)
                    .SetExtension(_remoteErrorField, error.RemoveException())
                    .SetExtension(_schemaNameErrorField, schemaName.Value);

                if (error.Path != null)
                {
                    Path path = RewriteErrorPath(error, context.Path);
                    builder.SetPath(path)
                        .ClearLocations()
                        .AddLocation(context.FieldSelection);
                }
                else if (IsHttpError(error))
                {
                    builder.SetPath(context.Path)
                        .ClearLocations()
                        .AddLocation(context.FieldSelection);
                }

                context.ReportError(builder.Build());
            }
        }

        private static Path RewriteErrorPath(IError error, Path path)
        {
            Path current = path;

            if (error.Path.Count > 0
                && error.Path[0] is string s
                && current.Name.Equals(s))
            {
                for (int i = 1; i < error.Path.Count; i++)
                {
                    if (error.Path[i] is string name)
                    {
                        current = current.Append(name);
                    }

                    if (error.Path[i] is int index)
                    {
                        current = current.Append(index);
                    }
                }
            }

            return current;
        }

        private static bool IsHttpError(IError error) =>
            error.Code == ErrorCodes.HttpRequestException;

        private static IReadOnlyCollection<Delegation.VariableValue> CreateVariableValues(
            IMiddlewareContext context,
            NameString schemaName,
            IEnumerable<Delegation.VariableValue> scopedVariables,
            ExtractedField extractedField,
            ExtractFieldQuerySyntaxRewriter rewriter)
        {
            var values = new Dictionary<string, Delegation.VariableValue>();

            foreach (Delegation.VariableValue value in scopedVariables)
            {
                values[value.Name] = value;
            }

            IReadOnlyDictionary<string, IValueNode> requestVariables = context.GetVariables();

            foreach (Delegation.VariableValue value in ResolveUsedRequestVariables(
                context.Schema, schemaName, extractedField,
                requestVariables, rewriter))
            {
                values[value.Name] = value;
            }

            return values.Values;
        }

        private static IReadOnlyList<Delegation.VariableValue> ResolveScopedVariables(
            IResolverContext context,
            NameString schemaName,
            OperationType operationType,
            IEnumerable<SelectionPathComponent> components,
            ExtractFieldQuerySyntaxRewriter rewriter)
        {
            var variables = new List<Delegation.VariableValue>();

            IStitchingContext stitchingContext = context.Service<IStitchingContext>();
            ISchema remoteSchema = stitchingContext.GetRemoteSchema(schemaName);
            IComplexOutputType type = remoteSchema.GetOperationType(operationType);
            SelectionPathComponent[] comps = components.Reverse().ToArray();

            for (int i = 0; i < comps.Length; i++)
            {
                SelectionPathComponent component = comps[i];

                if (!type.Fields.TryGetField(component.Name.Value, out IOutputField field))
                {
                    throw new QueryException(new Error
                    {
                        Message = string.Format(
                            CultureInfo.InvariantCulture,
                            StitchingResources.DelegationMiddleware_PathElementInvalid,
                            component.Name.Value,
                            type.Name)
                    });
                }

                ResolveScopedVariableArguments(
                    context, schemaName, component,
                    field, variables, rewriter);

                if (i + 1 < comps.Length)
                {
                    if (!field.Type.IsComplexType())
                    {
                        throw new QueryException(new Error
                        {
                            Message = StitchingResources
                                .DelegationMiddleware_PathElementTypeUnexpected
                        });
                    }
                    type = (IComplexOutputType)field.Type.NamedType();
                }
            }

            return variables;
        }

        private static void ResolveScopedVariableArguments(
            IResolverContext context,
            NameString schemaName,
            SelectionPathComponent component,
            IOutputField field,
            ICollection<Delegation.VariableValue> variables,
            ExtractFieldQuerySyntaxRewriter rewriter)
        {
            foreach (ArgumentNode argument in component.Arguments)
            {
                if (!field.Arguments.TryGetField(argument.Name.Value, out IInputField arg))
                {
                    throw new QueryException(
                        ErrorBuilder.New()
                            .SetMessage(
                                StitchingResources.DelegationMiddleware_ArgumentNotFound,
                                argument.Name.Value)
                            .SetExtension("argument", argument.Name.Value)
                            .SetCode(ErrorCodes.ArgumentNotFound)
                            .Build());
                }

                if (argument.Value is ScopedVariableNode sv)
                {
                    Delegation.VariableValue variable =
                        _resolvers.Resolve(context, sv, arg.Type);
                    IValueNode value = rewriter.RewriteValueNode(
                        schemaName, arg.Type, variable.Value);
                    variables.Add(variable.WithValue(value));
                }
            }
        }

        private static IEnumerable<Delegation.VariableValue> ResolveUsedRequestVariables(
            ISchema schema,
            NameString schemaName,
            ExtractedField extractedField,
            IReadOnlyDictionary<string, IValueNode> requestVariables,
            ExtractFieldQuerySyntaxRewriter rewriter)
        {
            foreach (VariableDefinitionNode variable in extractedField.Variables)
            {
                string name = variable.Variable.Name.Value;
                INamedInputType namedType = schema.GetType<INamedInputType>(
                    variable.Type.NamedType().Name.Value);

                if (!requestVariables.TryGetValue(name, out IValueNode value))
                {
                    value = NullValueNode.Default;
                }

                value = rewriter.RewriteValueNode(
                    schemaName,
                    (IInputType)variable.Type.ToType(namedType),
                    value);

                yield return new Delegation.VariableValue
                (
                    name,
                    variable.Type,
                    value,
                    variable.DefaultValue
                );
            }
        }

        private static void AddVariables(
            IQueryRequestBuilder builder,
            DocumentNode query,
            IEnumerable<Delegation.VariableValue> variableValues)
        {
            OperationDefinitionNode operation =
                query.Definitions.OfType<OperationDefinitionNode>().First();

            var usedVariables = new HashSet<string>(
                operation.VariableDefinitions.Select(t =>
                    t.Variable.Name.Value));

            foreach (Delegation.VariableValue variableValue in variableValues)
            {
                if (usedVariables.Contains(variableValue.Name))
                {
                    builder.AddVariableValue(variableValue.Name, variableValue.Value);
                }
            }
        }

        private static IInputType WrapType(
            IInputType namedType,
            ITypeNode typeNode)
        {
            if (typeNode is NonNullTypeNode nntn)
            {
                return new NonNullType(WrapType(namedType, nntn.Type));
            }
            else if (typeNode is ListTypeNode ltn)
            {
                return new ListType(WrapType(namedType, ltn.Type));
            }
            else
            {
                return namedType;
            }
        }

        private static IReadOnlyList<VariableDefinitionNode> CreateVariableDefs(
            IReadOnlyCollection<Delegation.VariableValue> variableValues)
        {
            var definitions = new List<VariableDefinitionNode>();

            foreach (Delegation.VariableValue variableValue in variableValues)
            {
                definitions.Add(new VariableDefinitionNode(
                    null,
                    new VariableNode(new NameNode(variableValue.Name)),
                    variableValue.Type,
                    variableValue.DefaultValue,
                    Array.Empty<DirectiveNode>()
                ));
            }

            return definitions;
        }
    }
}
