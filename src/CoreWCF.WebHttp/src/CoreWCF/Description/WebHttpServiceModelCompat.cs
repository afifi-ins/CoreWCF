﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using CoreWCF.Collections.Generic;
using CoreWCF.Web;

namespace CoreWCF.Description;

/// <summary>
/// This class contains the logic to convert System.ServiceModel.Web attributes into
/// CoreWCF.WebHttp attributes.
/// This allows endpoints to be defined using WCF classic attributes as well as CoreWCF
/// attributes, for a simpler migration path.
/// </summary>
internal static class WebHttpServiceModelCompat
{

    public static void ServiceModelAttributeFixup(ServiceEndpoint endpoint)
    {
        foreach (OperationDescription operationDescription in endpoint.Contract.Operations)
        {
            if (operationDescription.OperationBehaviors is KeyedByTypeCollection<IOperationBehavior> behaviors
                && behaviors.Find<WebGetAttribute>() == null
                && behaviors.Find<WebInvokeAttribute>() == null)
            {
                CheckForAndConvertSmAttributes(operationDescription);
            }
        }
    }

    private static void CheckForAndConvertSmAttributes(OperationDescription od)
    {
        var opMethod = od.SyncMethod ?? od.TaskMethod ?? od.BeginMethod;
        var attributes = opMethod.GetCustomAttributes().ToArray();

        var convertedAttributes = AttributeConverters.Convert(attributes);
        foreach(var converted in convertedAttributes)
            od.OperationBehaviors.Add(converted);

    }

    private static class AttributeConverters
    {

        private static readonly IReadOnlyDictionary<string, IAttributeConverter> s_attributeConverters =
            new Dictionary<string, IAttributeConverter>
            {
                { "System.ServiceModel.Web.WebGetAttribute", new AttributeConverter<WebGetAttribute>() },
                { "System.ServiceModel.Web.WebInvokeAttribute", new AttributeConverter<WebInvokeAttribute>() }
            };

        public static IEnumerable<IOperationBehavior> Convert(IEnumerable<Attribute> attributes)
        {
            foreach (Attribute attribute in attributes)
            {
                if (Convert(attribute) is { } convertedAttribute)
                    yield return convertedAttribute;
            }
        }

        private static IOperationBehavior? Convert(Attribute attribute)
        {
            if (s_attributeConverters.TryGetValue(attribute.GetType().FullName, out var converter))
            {
                return converter.Convert(attribute);
            }

            return null;
        }
    }

    private interface IAttributeConverter
    {
        IOperationBehavior? Convert(Attribute attribute);
    }

    private class AttributeConverter<TOut> : IAttributeConverter where TOut : class, IOperationBehavior
    {
        private static readonly ConcurrentDictionary<Type, Func<Attribute, TOut?>> s_converterCache = new();

        private static readonly MethodInfo s_buildDynamicMethodInfo = typeof(AttributeConverter<TOut>)
            .GetMethod(nameof(BuildDynamic), BindingFlags.Static | BindingFlags.NonPublic)!;


        public IOperationBehavior? Convert(Attribute attribute)
        {
            var type = attribute.GetType();

            if (!s_converterCache.TryGetValue(type, out var converter))
            {
                converter = s_converterCache[type] = Build(type);
            }


            return converter(attribute);
        }

        /// <summary>
        /// Creates a delegate that pattern matches for one type of attribute
        /// then converts it to the equivalent CoreWcf attribute.
        ///
        /// Since TInput comes from a reflected assembly, we only get Type,
        /// rather than a strong generic parameter. This means we need to
        /// use reflection on Type.
        /// </summary>
        /// <param name="inputType">The source attribute type</param>
        /// <returns>A CoreWCF attribute</returns>
        /// <typeparam name="TOut">The converted attribute type</typeparam>
        private static Func<Attribute, TOut?> Build(Type inputType)
        {
            return (Func<Attribute, TOut?>) s_buildDynamicMethodInfo
                .MakeGenericMethod(inputType)
                .Invoke(null, Array.Empty<object>());
        }

        private static Func<Attribute, TOut?> BuildDynamic<TInput>()
        {
            var inputExpression = Expression.Parameter(typeof(TInput));

            // For each property build a get-set pair to copy properties
            // to the target value.
            var memberBindings =
                from inputProp in typeof(TInput).GetProperties()
                join outputProp in typeof(TOut).GetProperties() on inputProp.Name equals outputProp.Name
                where inputProp.CanRead
                where outputProp.CanWrite
                // Since we are dealing with Attributes, only primitive types are available
                // strings, numbers, and enums.
                // Therefore a simple cast expression should convert the enums simply.
                let value = Expression.Convert(Expression.Property(inputExpression, inputProp), outputProp.PropertyType)
                select (MemberBinding)Expression.Bind(outputProp, value);

            var convert = Expression.Lambda<Func<TInput, TOut>>(
                Expression.MemberInit(
                    Expression.New(typeof(TOut)),
                    memberBindings.ToArray()
                ),
                inputExpression
            ).Compile();

            return attribute =>
            {
                if (attribute is TInput input)
                {
                    // This is the same as
                    // return new CoreWCF.WebHttp.TOutAttribute()
                    // {
                    //     Foo = (CoreWCF.WebHttp.SomeEnum)input.Foo,
                    //     Bar = (CoreWCF.WebHttp.SomeEnum)input.Bar,
                    //     Baz = (CoreWCF.WebHttp.SomeEnum)input.Baz,
                    // };
                    return convert(input);
                }

                return null;
            };
        }
    }
}