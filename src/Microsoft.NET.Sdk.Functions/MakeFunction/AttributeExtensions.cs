﻿using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.NET.Sdk.Functions.MakeFunction;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;

namespace MakeFunctionJson
{
    internal static class AttributeExtensions
    {
        /// <summary>
        /// {Name}Attribute -> name
        /// </summary>
        /// <param name="attribute"></param>
        /// <returns></returns>
        public static string ToAttributeFriendlyName(this Attribute attribute)
        {
            const string suffix = nameof(Attribute);
            var name = attribute.GetType().Name;
            name = name.Substring(0, name.Length - suffix.Length);
            return name.ToLowerFirstCharacter();
        }

        private static readonly HashSet<string> _supportedAttributes = new HashSet<string>
         {
             // These 2 attributes are not handled currently.
             // They can go either on class, method, or parameter.
             // The code flow now assumes 1:1 mapping of attributes on parameters to function.json binding.
             // "StorageAccountAttribute",
             // "ServiceBusAccountAttribute",

             "BlobAttribute",
             "BlobTriggerAttribute",
             "QueueAttribute",
             "QueueTriggerAttribute",
             "TableAttribute",
             "EventHubAttribute",
             "EventHubTriggerAttribute",
             "TimerTriggerAttribute",
             "DocumentDBAttribute",
             "ApiHubTableAttribute",
             "MobileTableAttribute",
             "ServiceBusTriggerAttribute",
             "ServiceBusAttribute",
             "TwilioSmsAttribute",
             "NotificationHubAttribute",
             "HttpTriggerAttribute"
         };

        /// <summary>
        /// 
        /// </summary>
        /// <param name="attribute"></param>
        /// <returns></returns>
        public static bool IsWebJobsAttribute(this Attribute attribute)
        {
#if NET46
            return attribute.GetType().GetCustomAttributes().Any(a => a.GetType().FullName == "Microsoft.Azure.WebJobs.Description.BindingAttribute")
                || _supportedAttributes.Contains(attribute.GetType().Name);
#else
            return attribute.GetType().GetTypeInfo().GetCustomAttributes().Any(a => a.GetType().FullName == "Microsoft.Azure.WebJobs.Description.BindingAttribute")
                || _supportedAttributes.Contains(attribute.GetType().Name);
#endif
        }

        /// <summary>
        /// For every binding (which is what the returned JObject represents) there are 3 special keys:
        ///     "name" -> that is the parameter name, not set by this function
        ///     "type" -> that is the binding type. This is derived from the Attribute.Name itself. <see cref="AttributeExtensions.ToAttributeFriendlyName(Attribute)"/>
        ///     "direction" -> default is 'out'
        ///                    if the binding is "Trigger", then it's an in
        ///                    if the binding Attribute has a FileAccess property on it, then map it to that.
        /// a side from these 3, all the others are direct serialization of all of the attribute's properties.
        /// The mapping however isn't 1:1 in terms of the naming. Therefore, <see cref="NormalizePropertyName(string, PropertyInfo)"/>
        /// </summary>
        /// <param name="attribute"></param>
        /// <returns></returns>
        public static JObject ToJObject(this Attribute attribute)
        {
            var obj = new JObject
            {
                // the friendly name is basically the name without 'Attribute' suffix and lowerCase first Char.
                ["type"] = attribute.ToAttributeFriendlyName()
            };

            // Default value is out
            var direction = Direction.@out;
            if (obj["type"].ToString().IndexOf("Trigger") > 0)
            {
                // if binding.type is trigger, then it's 'in'
                direction = Direction.@in;
            }

            foreach (var property in attribute
                                    .GetType()
                                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                    .Where(p => p.CanRead && p.PropertyType != typeof(System.Object)))
            {
                var propertyValue = property.GetValue(attribute);

                if (propertyValue == null || (propertyValue is int && (int)propertyValue == 0))
                {
                    // Don't serialize null properties and int properties for some reason.
                    // the int handling logic was copied from Mike's > "Table.Take is not nullable. So 0 means ignore"
                    continue;
                }

                var propertyType = property.PropertyType;
#if NET46
                if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(Nullable<>))
#else
                if (propertyType.GetTypeInfo().IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(Nullable<>))
#endif
                {
                    // Unwrap nullable types to their underlying type.
                    propertyType = Nullable.GetUnderlyingType(propertyType);
                }

                // What about other Enums?
                if (propertyType == typeof(FileAccess))
                {
                    // FileAccess on the Attribute dictates the "direction" property.
                    Direction convert(FileAccess value)
                    {
                        if (value == FileAccess.Read)
                        {
                            return Direction.@in;
                        }
                        else if (value == FileAccess.Write)
                        {
                            return Direction.@out;
                        }
                        else
                        {
                            return Direction.inout;
                        }
                    }
                    direction = convert((FileAccess)propertyValue);
                    continue;
                }

                // Check if property is supported.
                CheckIfPropertyIsSupported(attribute.GetType().Name, property);

                // Normalize and store the propertyName
                var propertyName = NormalizePropertyName(attribute.GetType().Name, property);
                if (TryGetPropertyValue(property, propertyValue, out string jsonValue))
                {
                    obj[propertyName] = jsonValue;
                }
                else
                {
                    obj[propertyName] = JToken.FromObject(propertyValue);
                }
            }

            // Serialize the direction
            obj["direction"] = direction.ToString();
            return obj;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="attribute"></param>
        /// <param name="propertyName"></param>
        /// <returns></returns>
        public static T GetValue<T>(this Attribute attribute, string propertyName)
        {
            var property = attribute.GetType().GetProperty(propertyName);
            if (property != null)
            {
                return (T)property.GetValue(attribute);
            }
            else
            {
                return default(T);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="attribute"></param>
        /// <param name="propertyName"></param>
        /// <param name="propertyValue"></param>
        public static void SetValue(this Attribute attribute, string propertyName, object propertyValue)
        {
            var property = attribute.GetType().GetProperty(propertyName);
            if (property != null)
            {
                property.SetValue(attribute, propertyValue);
            }
        }

        private static bool TryGetPropertyValue(PropertyInfo property, object propertyValue, out string value)
        {
            value = null;
#if NET46
            if (property.PropertyType.IsEnum)
#else
            if (property.PropertyType.GetTypeInfo().IsEnum)
#endif
            {
                value = Enum.GetName(property.PropertyType, propertyValue).ToLowerFirstCharacter();
                return true;
            }
            return false;
        }

        private static void CheckIfPropertyIsSupported(string attributeName, PropertyInfo property)
        {
            var propertyName = property.Name;
            if (attributeName == "TimerTriggerAttribute")
            {
                if (propertyName == "ScheduleType")
                {
                    throw new NotImplementedException($"Property '{propertyName}' on attribute '{attributeName}' is not supported in Azure Functions.");
                }
            }
        }

        /// <summary>
        /// These exceptions are coming from how the script runtime is reading function.json
        /// See https://github.com/Azure/azure-webjobs-sdk-script/tree/dev/src/WebJobs.Script/Binding
        /// If there are no exceptions for a given property name on a given attribute, then return it's name with a lowerCase first character.
        /// </summary>
        /// <param name="attributeName"></param>
        /// <param name="property"></param>
        /// <returns></returns>
        private static string NormalizePropertyName(string attributeName, PropertyInfo property)
        {
            var propertyName = property.Name;

            if ((attributeName == "BlobAttribute") || (attributeName == "BlobTriggerAttribute"))
            {
                if (propertyName == "BlobPath")
                {
                    return "path";
                }
            }
            else if (attributeName == "MobileTableAttribute")
            {
                if (propertyName == "MobileAppUriSetting")
                {
                    return "connection";
                }
                else if (propertyName == "ApiKeySetting")
                {
                    return "apiKey";
                }
            }
            else if (attributeName == "NotificationHubAttribute")
            {
                if (propertyName == "ConnectionStringSetting")
                {
                    return "connection";
                }
            }
            else if (attributeName == "ServiceBusAttribute")
            {
                if (propertyName == "QueueOrTopicName")
                {
                    // The attribute has a QueueOrTopicName while function.json has distinct queue and topic.
                    // I just picked queue.
                    return "queue";
                }
            }
            else if (attributeName == "TwilioSmsAttribute")
            {
                if (propertyName == "AccountSidSetting")
                {
                    return "accountSid";
                }
                else if (propertyName == "AuthTokenSetting")
                {
                    return "authToken";
                }
            }
            else if (attributeName == "TimerTriggerAttribute")
            {
                if (propertyName == "ScheduleExpression")
                {
                    return "schedule";
                }
            }
            else if (attributeName == "EventHubTriggerAttribute")
            {
                if (propertyName == "EventHubName")
                {
                    return "path";
                }
            }
            else if (attributeName == "DocumentDBAttribute")
            {
                if (propertyName == "ConnectionStringSetting")
                {
                    return "connection";
                }
            }

            return propertyName.ToLowerFirstCharacter();
        }
    }
}
