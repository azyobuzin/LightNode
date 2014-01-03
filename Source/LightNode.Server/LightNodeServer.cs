﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LightNode.Server
{
    internal class LightNodeServer
    {
        readonly Dictionary<RequestPath, OperationHandler> handlers = new Dictionary<RequestPath, OperationHandler>();

        readonly LightNodeOptions options;

        int alreadyRegistered = -1;

        public LightNodeServer(LightNodeOptions options)
        {
            this.options = options;
        }

        // cache all methods
        public void RegisterHandler(Assembly[] hostAssemblies)
        {
            if (Interlocked.Increment(ref alreadyRegistered) != 0) return;

            var contractTypes = hostAssemblies
                .SelectMany(x => x.GetTypes())
                .Where(x => typeof(LightNodeContract).IsAssignableFrom(x));

            Parallel.ForEach(contractTypes, classType =>
            {
                var className = classType.Name;
                if (!classType.GetConstructors().Any(x => x.GetParameters().Length == 0))
                {
                    throw new InvalidOperationException(string.Format("Type needs parameterless constructor, class:{0}", classType.FullName));
                }

                foreach (var methodInfo in classType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (methodInfo.IsSpecialName && (methodInfo.Name.StartsWith("set_") || methodInfo.Name.StartsWith("get_"))) continue; // as property

                    var methodName = methodInfo.Name;

                    // ignore default methods
                    if (methodName == "Equals"
                     || methodName == "GetHashCode"
                     || methodName == "GetType"
                     || methodName == "ToString")
                    {
                        continue;
                    }

                    // create handler
                    var handler = new OperationHandler(options, classType, methodInfo);
                    lock (handlers)
                    {
                        // fail duplicate entry
                        var path = new RequestPath(className, methodName);
                        if (handlers.ContainsKey(path))
                        {
                            throw new InvalidOperationException(string.Format("same class and method is not allowed, class:{0} method:{1}", className, methodName));
                        }
                        handlers.Add(path, handler);
                    }
                }
            });
        }

        // Routing -> ParameterBinding -> Execute
        public async Task ProcessRequest(IDictionary<string, object> environment)
        {
            try
            {
                var path = environment["owin.RequestPath"] as string;

                // verb check
                var method = environment["owin.RequestMethod"];
                var verb = AcceptVerbs.Get;
                if (StringComparer.OrdinalIgnoreCase.Equals(method, "GET"))
                {
                    verb = AcceptVerbs.Get;
                }
                else if (StringComparer.OrdinalIgnoreCase.Equals(method, "POST"))
                {
                    verb = AcceptVerbs.Post;
                }
                else
                {
                    environment.EmitMethodNotAllowed();
                    return;
                }

                var keyBase = path.Trim('/').Split('/');
                if (keyBase.Length != 2)
                {
                    environment.EmitNotFound();
                    return;
                }

                // extract "extension" for media type
                var extStart = keyBase[1].LastIndexOf(".");
                var ext = "";
                if (extStart != -1)
                {
                    ext = keyBase[1].Substring(extStart + 1);
                    keyBase[1] = keyBase[1].Substring(0, keyBase[1].Length - ext.Length - 1);
                }

                // {ClassName, MethodName}
                var key = new RequestPath(keyBase[0], keyBase[1]);

                OperationHandler handler;
                if (handlers.TryGetValue(key, out handler))
                {
                    // verb check
                    if (!options.DefaultAcceptVerb.HasFlag(verb))
                    {
                        environment.EmitMethodNotAllowed();
                        return;
                    }

                    // Parameter binding
                    var methodParameters = options.parametertBinder.BindParameter(environment, options, handler.Arguments);
                    if (methodParameters == null)
                    {
                        return;
                    }

                    // select formatter
                    var requestHeader = environment["owin.RequestHeaders"] as IDictionary<string, string[]>;
                    string[] accepts;

                    var formatter = options.DefaultFormatter;
                    if (ext != "")
                    {
                        formatter = new[] { options.DefaultFormatter }.Concat(options.SpecifiedFormatters)
                            .FirstOrDefault(x => x.Ext == ext);

                        if (formatter == null)
                        {
                            environment.EmitNotAcceptable();
                            return;
                        }
                    }
                    else if (requestHeader.TryGetValue("Accept", out accepts))
                    {
                        // TODO:parse accept header q, */*, etc...
                        var contentType = accepts[0];
                        formatter = new[] { options.DefaultFormatter }.Concat(options.SpecifiedFormatters)
                            .FirstOrDefault(x => contentType.Contains(x.MediaType));

                        if (formatter == null)
                        {
                            formatter = options.DefaultFormatter; // through...
                        }
                    }

                    // Operation execute
                    var context = new OperationContext(environment, handler.ClassName, handler.MethodName, verb)
                    {
                        Parameters = methodParameters,
                        ContentFormatter = formatter,
                        Filters = handler.FiltersLookup
                    };
                    await handler.Execute(options, context).ConfigureAwait(false);
                    return;
                }
                else
                {
                    environment.EmitNotFound();
                    return;
                }
            }
            catch (ReturnStatusCodeException statusException)
            {
                statusException.EmitCode(environment);
                return;
            }
            catch (Exception ex)
            {
                switch (options.ErrorHandlingPolicy)
                {
                    case ErrorHandlingPolicy.ReturnInternalServerError:
                        environment.EmitInternalServerError();
                        return;
                    case ErrorHandlingPolicy.ReturnInternalServerErrorIncludeErrorDetails:
                        environment.EmitInternalServerError();
                        environment.EmitStringMessage(ex.ToString());
                        return;
                    case ErrorHandlingPolicy.ThrowException:
                    default:
                        throw;
                }
            }
        }
    }
}