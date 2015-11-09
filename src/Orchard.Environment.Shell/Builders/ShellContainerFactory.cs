﻿using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orchard.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orchard.Environment.Shell.Builders.Models;
using YesSql.Core.Indexes;
using YesSql.Core.Services;
using System.Data.SqlClient;
using YesSql.Core.Storage.InMemory;
using System.Data;
using Orchard.Events;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Reflection;
using Orchard.Environment.Extensions.Models;
using Orchard.Environment.Extensions.Features;
using Orchard.Environment.Extensions;
using Orchard.FileSystem.AppData;
using System.IO;
using YesSql.Core.Storage.FileSystem;

namespace Orchard.Environment.Shell.Builders
{
    public class ShellContainerFactory : IShellContainerFactory
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IAppDataFolderRoot _appDataFolderRoot;

        public ShellContainerFactory(
            IServiceProvider serviceProvider,
            ILoggerFactory loggerFactory,
            IAppDataFolderRoot appDataFolderRoot)
        {
            _serviceProvider = serviceProvider;
            _appDataFolderRoot = appDataFolderRoot;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<ShellContainerFactory>();
        }

        public IServiceProvider CreateContainer(ShellSettings settings, ShellBlueprint blueprint)
        {
            IServiceCollection serviceCollection = new ServiceCollection();

            serviceCollection.AddInstance(settings);
            serviceCollection.AddInstance(blueprint.Descriptor);
            serviceCollection.AddInstance(blueprint);

            // Sure this is right?
            serviceCollection.AddInstance(_loggerFactory);

            IServiceCollection moduleServiceCollection = new ServiceCollection();
            foreach (var dependency in blueprint.Dependencies
                .Where(t => typeof(IModule).IsAssignableFrom(t.Type)))
            {

                moduleServiceCollection.AddScoped(typeof(IModule), dependency.Type);
            }

            var featureByType = blueprint.Dependencies.ToDictionary(x => x.Type, x => x.Feature);

            var moduleServiceProvider = new FallbackServiceProvider(_serviceProvider, moduleServiceCollection);

            foreach (var service in moduleServiceProvider.GetServices<IModule>())
            {
                service.Configure(serviceCollection);
            }

            foreach (var dependency in blueprint.Dependencies
                .Where(t => !typeof(IModule).IsAssignableFrom(t.Type)))
            {
                foreach (var interfaceType in dependency.Type.GetInterfaces()
                    .Where(itf => typeof(IDependency).IsAssignableFrom(itf)))
                {
                    _logger.LogDebug("Type: {0}, Interface Type: {1}", dependency.Type, interfaceType);

                    if (typeof(ISingletonDependency).IsAssignableFrom(interfaceType))
                    {
                        serviceCollection.AddSingleton(interfaceType, dependency.Type);
                    }
                    else if (typeof(IUnitOfWorkDependency).IsAssignableFrom(interfaceType))
                    {
                        serviceCollection.AddScoped(interfaceType, dependency.Type);
                    }
                    else if (typeof(ITransientDependency).IsAssignableFrom(interfaceType))
                    {
                        serviceCollection.AddTransient(interfaceType, dependency.Type);
                    }
                    else
                    {
                        serviceCollection.AddScoped(interfaceType, dependency.Type);
                    }
                }
            }

            // Configure event handlers
            var eventBus = new DefaultOrchardEventBus();
            serviceCollection.AddInstance<IEventBus>(eventBus);
            
            // Configuring data access
            var indexes = blueprint
            .Dependencies
            .Where(x => typeof(IIndexProvider).IsAssignableFrom(x.Type))
            .Select(x => x.Type).ToArray();

            serviceCollection.AddSingleton<IStore>(serviceProvider =>
            {
                var store = new Store(cfg =>
                {
                    cfg.ConnectionFactory = new DbConnectionFactory<SqlConnection>(@"Data Source =.; Initial Catalog = test1; User Id=sa;Password=demo123!");
                    cfg.DocumentStorageFactory = new FileSystemDocumentStorageFactory(Path.Combine(_appDataFolderRoot.RootFolder, "Documents"));
                    //cfg.ConnectionFactory = new DbConnectionFactory<SqliteConnection>(@"Data Source=" + dbFileName + ";Cache=Shared");
                    //cfg.DocumentStorageFactory = new InMemoryDocumentStorageFactory();
                    cfg.IsolationLevel = IsolationLevel.ReadUncommitted;
                    //cfg.RunDefaultMigration();
                });

                store.RegisterIndexes(indexes);
                return store;

            });

            serviceCollection.AddScoped<ISession>(serviceProvider =>
            {
                var store = serviceProvider.GetRequiredService<IStore>();
                return store.CreateSession();
            });

            serviceCollection.AddInstance<ITypeFeatureProvider>(new TypeFeatureProvider(featureByType));

            // Register event handlers on the event bus
            var eventHandlers = blueprint
                .Dependencies
                .Select(t => t.Type)
                .Where(t => typeof(IEventHandler).IsAssignableFrom(t) && t.GetTypeInfo().IsClass)
                .ToArray();

            foreach (var handlerClass in eventHandlers)
            {
                serviceCollection.AddScoped(handlerClass);

                // Register dynamic proxies to intercept direct calls if an IEventHandler is resolved, dispatching the call to 
                // the event bus.

                foreach (var i in handlerClass.GetInterfaces().Where(t => typeof(IEventHandler).IsAssignableFrom(t)))
                {
                    var notifyProxy = DefaultOrchardEventBus.CreateProxy(i);
                    notifyProxy.EventBus = eventBus;
                    serviceCollection.AddInstance(i, notifyProxy);
                }
            }

            var shellServiceProvider = new FallbackServiceProvider(_serviceProvider, serviceCollection);
            
            // Register any IEventHandler method in the event bus
            foreach (var handlerClass in eventHandlers)
            {
                foreach (var handlerInterface in handlerClass.GetInterfaces().Where(x => typeof(IEventHandler).IsAssignableFrom(x)))
                {
                    foreach (var interfaceMethod in handlerInterface.GetMethods())
                    {
                        //var classMethod = handlerClass.GetMethods().Where(x => x.Name == interfaceMethod.Name && x.GetParameters().Length == interfaceMethod.GetParameters().Length).FirstOrDefault();
                        Func<IDictionary<string, object>, Task> d = (parameters) => DefaultOrchardEventBus.Invoke(parameters, shellServiceProvider, interfaceMethod, handlerClass);
                        eventBus.Subscribe(handlerInterface.Name + "." + interfaceMethod.Name, d);
                    }
                }

            }

            return shellServiceProvider;
        }
    }
}