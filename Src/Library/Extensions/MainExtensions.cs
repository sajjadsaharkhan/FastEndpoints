﻿using FastEndpoints.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Claims;

namespace FastEndpoints;

/// <summary>
/// provides extensions to easily bootstrap fastendpoints in the asp.net middleware pipeline
/// </summary>
public static class MainExtensions
{
    private class EndpointDefinition
    {
#pragma warning disable CS8618
        public Type EndpointType { get; set; }
        public Type? ValidatorType { get; set; }
        public EndpointSettings Settings { get; set; }
#pragma warning restore CS8618
    }

    private static EndpointDefinition[]? discoveredEndpointDefinitions;

    /// <summary>
    /// adds the FastEndpoints services to the ASP.Net middleware pipeline
    /// </summary>
    /// <param name="services"></param>
    public static IServiceCollection AddFastEndpoints(this IServiceCollection services)
    {
        Discover_Endpoints_Validators_EventHandlers();
        services.AddAuthorization(BuildSecurityPoliciesForEndpoints); //this method doesn't block
        return services;
    }

    /// <summary>
    /// finalizes auto discovery of endpoints and prepares FastEndpoints to start processing requests
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="ArgumentException"></exception>
    public static IEndpointRouteBuilder UseFastEndpoints(this IEndpointRouteBuilder builder)
    {
        if (discoveredEndpointDefinitions is null) throw new InvalidOperationException($"Please use .{nameof(AddFastEndpoints)}() first!");

        BaseEndpoint.SerializerOptions = builder.ServiceProvider.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        BaseEventHandler.ServiceProvider = builder.ServiceProvider;

        var routeToHandlerCounts = new Dictionary<string, int>();

        foreach (var ep in discoveredEndpointDefinitions)
        {
            var epName = ep.EndpointType.FullName;
            var epSettings = ep.Settings;

            if (epSettings.Verbs?.Any() != true) throw new ArgumentException($"No HTTP Verbs declared on: [{epName}]");
            if (epSettings.Routes?.Any() != true) throw new ArgumentException($"No Routes declared on: [{epName}]");

            var policiesToAdd = new List<string>();
            if (epSettings.PreBuiltUserPolicies?.Any() is true) policiesToAdd.AddRange(epSettings.PreBuiltUserPolicies);
            if (epSettings.Permissions?.Any() is true ||
                epSettings.Claims?.Any() is true ||
                epSettings.Roles?.Any() is true)
            {
                policiesToAdd.Add(SecurityPolicyName(ep.EndpointType));
            }

            var epFactory = Expression.Lambda<Func<object>>(Expression.New(ep.EndpointType)).Compile();

            var validatorInstance = (IValidatorWithState?)(ep.ValidatorType is null ? null : Activator.CreateInstance(ep.ValidatorType));
            if (validatorInstance is not null)
            {
                validatorInstance.ServiceProvider = builder.ServiceProvider;
                validatorInstance.ThrowIfValidationFails = epSettings.ThrowIfValidationFails;
            }

            EndpointExecutor.CachedServiceBoundProps[ep.EndpointType]
                = ep.EndpointType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            foreach (var route in epSettings.Routes)
            {
                foreach (var verb in epSettings.Verbs)
                {
                    var eb = builder.MapMethods(route, new[] { verb }, EndpointExecutor.HandleAsync);

                    if (epSettings.InternalConfigAction is not null) epSettings.InternalConfigAction(eb);//always do this first

                    if (epSettings.AnonymousVerbs?.Contains(verb) is true)
                        eb.AllowAnonymous();
                    else
                        eb.RequireAuthorization(policiesToAdd.ToArray());

                    if (epSettings.ResponseCacheSettings is not null) eb.WithMetadata(epSettings.ResponseCacheSettings);
                    if (epSettings.AllowFileUploads is true) eb.Accepts<IFormFile>("multipart/form-data");
                    if (epSettings.UserConfigAction is not null) epSettings.UserConfigAction(eb);//always do this last - allow user to override everything done above

                    var cacheKey = $"{verb}:{route}";

                    EndpointExecutor.CachedEndpointDefinitions[cacheKey]
                        = new(epFactory, validatorInstance, epSettings.PreProcessors, epSettings.PostProcessors);

                    routeToHandlerCounts.TryGetValue(cacheKey, out var count);
                    routeToHandlerCounts[cacheKey] = count + 1;
                }
            }
        }

        var logger = builder.ServiceProvider.GetRequiredService<ILogger<DuplicateHandlerRegistration>>();

        foreach (var kvp in routeToHandlerCounts)
            if (kvp.Value > 1) logger.LogError($"The route \"{kvp.Key}\" has {kvp.Value} endpoints registered to handle requests!");

        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            discoveredEndpointDefinitions = null;
        });

        return builder;
    }

    private static void BuildSecurityPoliciesForEndpoints(AuthorizationOptions opts)
    {
        //WARNING: don't assign anything to discovered endpoint definitions in this method
        //         because this delegate is not guranteed to run before UseEndpoints()

        if (discoveredEndpointDefinitions is null) throw new InvalidOperationException("Unable to discover any endpoint declarations!");

        foreach (var ep in discoveredEndpointDefinitions)
        {
            var eps = ep.Settings;

            if (eps.Roles is null && eps.Permissions is null && eps.Claims is null) continue;

            var secPolName = SecurityPolicyName(ep.EndpointType);

            opts.AddPolicy(secPolName, b =>
            {
                b.RequireAuthenticatedUser();

                if (eps.Permissions?.Any() is true)
                {
                    if (eps.AllowAnyPermission is true)
                    {
                        b.RequireAssertion(x =>
                        {
                            var prmClaimVal = x.User.FindFirstValue(Constants.PermissionsClaimType);
                            if (prmClaimVal is null) return false;
                            return prmClaimVal.Split(',').Intersect(eps.Permissions).Any();
                        });
                    }
                    else
                    {
                        b.RequireAssertion(x =>
                        {
                            var prmClaimVal = x.User.FindFirstValue(Constants.PermissionsClaimType);
                            if (prmClaimVal is null) return false;
                            return !eps.Permissions.Except(prmClaimVal.Split(',')).Any();
                        });
                    }
                }

                if (eps.Claims?.Any() is true)
                {
                    if (eps.AllowAnyClaim is true)
                    {
                        b.RequireAssertion(x =>
                        {
                            return x.User.Claims
                            .Select(c => c.Type)
                            .Intersect(eps.Claims)
                            .Any();
                        });
                    }
                    else
                    {
                        b.RequireAssertion(x =>
                        {
                            return !eps.Claims
                            .Except(x.User.Claims.Select(c => c.Type))
                            .Any();
                        });
                    }
                }

                if (eps.Roles?.Any() is true) b.RequireRole(eps.Roles);
            });
        }
    }

    private static void Discover_Endpoints_Validators_EventHandlers()
    {
        var excludes = new[]
            {
                    "Microsoft.",
                    "System.",
                    "FastEndpoints.",
                    "testhost",
                    "netstandard",
                    "Newtonsoft.",
                    "mscorlib",
                    "NuGet."
                };

#pragma warning disable CS8602
        var discoveredTypes = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a =>
                  !a.IsDynamic &&
                  !excludes.Any(n => a.FullName.StartsWith(n)))
            .SelectMany(a => a.GetTypes())
            .Where(t =>
                  !t.IsAbstract &&
                  !t.IsInterface &&
                  t.GetInterfaces().Intersect(new[] {
                          typeof(IEndpoint),
                          typeof(IValidator),
                          typeof(IEventHandler)
                  }).Any());
#pragma warning restore CS8602

        if (!discoveredTypes.Any())
            throw new InvalidOperationException("Unable to find any endpoint declarations!");

        //Endpoint<TRequest>
        //Validator<TRequest>

        var epList = new List<(Type tEndpoint, Type tRequest)>();

        //key: TRequest //val: TValidator
        var valDict = new Dictionary<Type, Type>();

        foreach (var type in discoveredTypes)
        {
            var interfacesOfType = type.GetInterfaces();

            if (interfacesOfType.Contains(typeof(IEventHandler)))
            {
                ((IEventHandler?)Activator.CreateInstance(type))?.Subscribe();
                continue;
            }
            if (interfacesOfType.Contains(typeof(IEndpoint)))
            {
                var tRequest = typeof(EmptyRequest);

                if (type.BaseType?.IsGenericType is true)
                    tRequest = type.BaseType?.GetGenericArguments()?[0] ?? tRequest;

                epList.Add((type, tRequest));
            }
            else
            {
#pragma warning disable CS8602
                Type tRequest = type.BaseType.GetGenericArguments()[0];
                valDict.Add(tRequest, type);
#pragma warning restore CS8602
            }
        }
#pragma warning disable CS8600, CS8601
        discoveredEndpointDefinitions = epList
            .Select(x =>
            {
                var instance = (IEndpoint)Activator.CreateInstance(x.tEndpoint);
                instance?.Configure();
                return new EndpointDefinition()
                {
                    EndpointType = x.tEndpoint,
                    ValidatorType = valDict.GetValueOrDefault(x.tRequest),
                    Settings = (EndpointSettings)BaseEndpoint.SettingsPropInfo.GetValue(instance)
                };
            })
            .ToArray();
#pragma warning restore CS8600, CS8601
    }

    private static string SecurityPolicyName(Type endpointType)
    {
        return $"epPolicy:{endpointType.FullName}";
    }
}

internal class DuplicateHandlerRegistration { }
