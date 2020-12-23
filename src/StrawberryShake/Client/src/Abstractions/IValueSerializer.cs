using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using StrawberryShake.Remove;

namespace StrawberryShake
{
    public interface IValueSerializerResolver
    {
        IValueSerializer<TData, TRuntime> GetValueSerializer<TData, TRuntime>(string name);
    }


    public interface IValueSerializer
    {

    }

    public interface IValueSerializer<in TData, out TRuntime> : IValueSerializer
    {
        TRuntime Deserialize(TData data);
    }


    // knows about operation store and entity store and data
    public interface IResultReader
    {
        //OperationResult<FooQueryResult> Parse(Stream stream);
    }
}
