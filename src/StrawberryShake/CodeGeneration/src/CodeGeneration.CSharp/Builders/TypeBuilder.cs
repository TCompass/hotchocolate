using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StrawberryShake.CodeGeneration.CSharp.Extensions;

namespace StrawberryShake.CodeGeneration.CSharp.Builders
{
    public class TypeBuilder : ICodeBuilder
    {
        private string? _name;
        private List<string> _genericTypeArguments = new List<string>();
        private ListType _isList = ListType.NoList;
        private bool _isNullable = false;
        public static TypeBuilder New() => new TypeBuilder();

        public TypeBuilder SetListType(ListType isList)
        {
            _isList = isList;
            return this;
        }

        public TypeBuilder SetName(string name)
        {
            _name = name;
            return this;
        }

        public TypeBuilder AddGeneric(string name)
        {
            _genericTypeArguments.Add(name);
            return this;
        }

        public TypeBuilder SetIsNullable(bool isNullable)
        {
            _isNullable = isNullable;
            return this;
        }

        public async Task BuildAsync(CodeWriter writer)
        {
            await writer.WriteAsync(_isList.IfListPrint("IReadOnlyList<")).ConfigureAwait(false);;
            await writer.WriteAsync(_name).ConfigureAwait(false);;
            if (_genericTypeArguments.Count > 0)
            {
                await writer.WriteAsync("<").ConfigureAwait(false);;
                for (var i = 0; i < _genericTypeArguments.Count; i++)
                {
                    if (i > 0)
                    {
                        await writer.WriteAsync(", ").ConfigureAwait(false);;
                    }
                    await writer.WriteAsync(_genericTypeArguments[i]).ConfigureAwait(false);;
                }
                await writer.WriteAsync(">").ConfigureAwait(false);;
            }

            if (_isNullable)
            {
                await writer.WriteAsync("?").ConfigureAwait(false);;
            }
            await writer.WriteAsync($"{_isList.IfListPrint(">")}{_isList.IfNullableListPrint("?")}").ConfigureAwait(false);
            await writer.WriteSpaceAsync().ConfigureAwait(false);
        }
    }
}
