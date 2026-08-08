using TrainingHub.Shared.Application.IntegrationEventHandlers;
using TrainingHub.Shared.Application.IntegrationEvents;
using TrainingHub.Shared.Application.Queries;
using TrainingHub.Shared.Common;
using TrainingHub.Shared.Domain.Aggregates.TrainerAggregate;
using TrainingHub.Shared.Domain.Aggregates.TrainingAggregate;
using TrainingHub.Shared.Infrastructure.Outbox;
using TrainingHub.Shared.Infrastructure.Queries;
using TrainingHub.Shared.Infrastructure.Repositories;
using TrainingHub.Shared.Infrastructure.Services;
using TrainingHub.Shared.Infrastructure.ThirdParty.EfCore;
using TrainingHub.Shared.Infrastructure.ThirdParty.EfCore.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace TrainingHub.Shared.Infrastructure.Extensions;

/// <summary>
/// Registers the infrastructure layer.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the database session, the repositories and the unit of work.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Every outbox knob is checked positive before the host serves anything, in the mould of
        // SmtpOptions and ObjectStorageOptions: fail at start-up rather than on the first drain.
        // The defaults all pass, so a host with no section keeps starting (ADR 0033).
        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.SectionName))
            .Validate(
                outbox => outbox.PollInterval > TimeSpan.Zero,
                $"'{OutboxOptions.SectionName}:{nameof(OutboxOptions.PollInterval)}' must be positive.")
            .Validate(
                outbox => outbox.BatchSize > 0,
                $"'{OutboxOptions.SectionName}:{nameof(OutboxOptions.BatchSize)}' must be positive.")
            .Validate(
                outbox => outbox.MaxAttempts > 0,
                $"'{OutboxOptions.SectionName}:{nameof(OutboxOptions.MaxAttempts)}' must be positive.")
            .Validate(
                outbox => outbox.LeaseDuration > TimeSpan.Zero,
                $"'{OutboxOptions.SectionName}:{nameof(OutboxOptions.LeaseDuration)}' must be positive.")
            .Validate(
                outbox => outbox.RetryDelay > TimeSpan.Zero,
                $"'{OutboxOptions.SectionName}:{nameof(OutboxOptions.RetryDelay)}' must be positive.")
            .Validate(
                outbox => outbox.RetentionPeriod > TimeSpan.Zero,
                $"'{OutboxOptions.SectionName}:{nameof(OutboxOptions.RetentionPeriod)}' must be positive.")
            .ValidateOnStart();

        return services
            .AddScoped<IDomainEventDispatcher, MediatorDomainEventDispatcher>()
            .AddScoped<IUnitOfWork, UnitOfWork>()
            .AddScoped<ITrainerRepository, TrainerRepository>()
            .AddScoped<ITrainingRepository, TrainingRepository>()
            // Resolved through ITrainingRepository rather than registered a second time against
            // the same implementation: two registrations mean two instances per request, and the
            // only reason that was harmless is that both happened to share the DbContext.
            .AddScoped<IUniquenessTitleChecker>(serviceProvider =>
                (TrainingRepository)serviceProvider.GetRequiredService<ITrainingRepository>())
            .AddScoped<ITrainingCounter>(serviceProvider =>
                (TrainingRepository)serviceProvider.GetRequiredService<ITrainingRepository>())
            // The standing port answers about a trainer, so it is the trainer's repository that
            // implements it — resolved through ITrainerRepository for the same reason as above.
            .AddScoped<ITrainerStanding>(serviceProvider =>
                (TrainerRepository)serviceProvider.GetRequiredService<ITrainerRepository>())
            // The read side of four questions that used to cost a whole aggregate, or would have:
            // who owns this training, which trainer is behind this Identity user, where a trainer
            // is reachable as a person, and what a page of trainers is called. Each answer is a
            // handful of columns, and none of these ports can write — which is what a post-commit
            // consumer may hold (ADR 0056).
            .AddScoped<ITrainingOwnerQuery, TrainingOwnerQuery>()
            .AddScoped<ITrainerIdentityQuery, TrainerIdentityQuery>()
            .AddScoped<ITrainerAccountQuery, TrainerAccountQuery>()
            .AddScoped<ITrainerNamesQuery, TrainerNamesQuery>()
            // Scoped like the DbContext it stages rows into: the publisher must share the unit of
            // work of the save that is dispatching the domain events (ADR 0002).
            .AddScoped<IIntegrationEventPublisher, OutboxIntegrationEventPublisher>()
            // The outbox's read side (ADR 0025, hardened per ADR 0033): the worker each host
            // runs, the processor it scopes per batch, the dispatcher that routes a fact to its
            // consumers, and the fourteen consumers themselves — the policies that used to run
            // inside the transaction, reattached after the commit, and the six that answer a
            // sanction (ADR 0056). The options bind above, validated.
            .AddScoped<OutboxProcessor>()
            .AddScoped<IntegrationEventDispatcher>()
            .AddScoped<IIntegrationEventHandler<TrainerCreatedIntegrationEvent>,
                SendWelcomeEmailWhenTrainerCreatedIntegrationEventHandler>()
            .AddScoped<IIntegrationEventHandler<TrainerContactEmailChangedIntegrationEvent>,
                NotifyPreviousAddressWhenTrainerContactEmailChangedIntegrationEventHandler>()
            .AddScoped<IIntegrationEventHandler<TrainerSuspendedIntegrationEvent>,
                SendSuspensionNoticeWhenTrainerSuspendedIntegrationEventHandler>()
            .AddScoped<IIntegrationEventHandler<TrainerSuspendedIntegrationEvent>,
                HideCatalogueWhenTrainerSuspendedIntegrationEventHandler>()
            .AddScoped<IIntegrationEventHandler<TrainerReinstatedIntegrationEvent>,
                SendReinstatementNoticeWhenTrainerReinstatedIntegrationEventHandler>()
            .AddScoped<IIntegrationEventHandler<TrainerReinstatedIntegrationEvent>,
                ShowCatalogueWhenTrainerReinstatedIntegrationEventHandler>()
            .AddScoped<IIntegrationEventHandler<TrainingCreatedIntegrationEvent>,
                IndexTrainingWhenTrainingCreatedIntegrationEventHandler>()
            .AddScoped<IIntegrationEventHandler<TrainingEditedIntegrationEvent>,
                ReindexTrainingWhenTrainingEditedIntegrationEventHandler>()
            .AddScoped<IIntegrationEventHandler<TrainingTransferredIntegrationEvent>,
                ReindexTrainingWhenTrainingTransferredIntegrationEventHandler>()
            .AddScoped<IIntegrationEventHandler<TrainingPublishedIntegrationEvent>,
                IndexTrainingWhenTrainingPublishedIntegrationEventHandler>()
            .AddScoped<IIntegrationEventHandler<TrainingUnpublishedIntegrationEvent>,
                RemoveTrainingFromIndexWhenTrainingUnpublishedIntegrationEventHandler>()
            .AddScoped<IIntegrationEventHandler<TrainingWithheldIntegrationEvent>,
                SendWithholdingNoticeWhenTrainingWithheldIntegrationEventHandler>()
            .AddScoped<IIntegrationEventHandler<TrainingWithheldIntegrationEvent>,
                RemoveTrainingFromIndexWhenTrainingWithheldIntegrationEventHandler>()
            .AddScoped<IIntegrationEventHandler<TrainingDeletedIntegrationEvent>,
                RemoveTrainingFromIndexWhenTrainingDeletedIntegrationEventHandler>()
            .AddHostedService<OutboxDeliveryWorker>()
            .AddDbContext<TrainingContext>((serviceProvider, options) =>
            {
                options.UseSqlServer(configuration.GetConnectionString("TrainingContext"))
                    .AddInterceptors(
                        serviceProvider.GetRequiredService<DomainEventInterceptor>(),
                        serviceProvider.GetRequiredService<AuditableEntitiesInterceptor>());

                // Parameter values (emails, names, bios…) only ever reach the
                // logs in Development; production logs stay free of personal data.
                if (serviceProvider.GetRequiredService<IHostEnvironment>().IsDevelopment())
                {
                    options.EnableSensitiveDataLogging();
                }
            })
            .AddObjectStorage(configuration)
            // The email port's real adapter (ADR 0031), called by the outbox's consumers after the
            // commit — the worker delivers the facts, and this port acts on them (ADR 0002, ADR 0025).
            .AddEmailDelivery(configuration)
            .AddScoped<DomainEventInterceptor>()
            // The system clock, injected so the audit stamps can be driven by a test.
            .AddSingleton(TimeProvider.System)
            .AddSingleton<AuditableEntitiesInterceptor>()
            // The search port, consumed the same way, is still a fake that writes to the log:
            // choosing an indexer stays a one-line change here.
            .AddSingleton<ITrainingSearchIndexer, FakeTrainingSearchIndexer>();
    }
}
