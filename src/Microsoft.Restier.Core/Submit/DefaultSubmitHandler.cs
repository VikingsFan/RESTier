﻿// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Restier.Core.Properties;

namespace Microsoft.Restier.Core.Submit
{
    /// <summary>
    /// Represents the default submit handler.
    /// </summary>
    internal static class DefaultSubmitHandler
    {
        /// <summary>
        /// Asynchronously executes the submit flow.
        /// </summary>
        /// <param name="context">
        /// The submit context.
        /// </param>
        /// <param name="cancellationToken">
        /// A cancellation token.
        /// </param>
        /// <returns>
        /// A task that represents the asynchronous
        /// operation whose result is a submit result.
        /// </returns>
        public static async Task<SubmitResult> SubmitAsync(
            SubmitContext context, CancellationToken cancellationToken)
        {
            Ensure.NotNull(context, "context");

            var preparer = context.GetApiService<IChangeSetInitializer>();
            if (preparer == null)
            {
                throw new NotSupportedException(Resources.ChangeSetPreparerMissing);
            }

            await preparer.InitializeAsync(context, cancellationToken);

            if (context.Result != null)
            {
                return context.Result;
            }

            var eventsChangeSet = context.ChangeSet;

            IEnumerable<ChangeSetItem> currentChangeSetItems = eventsChangeSet.Entries.ToArray();

            await PerformValidate(context, currentChangeSetItems, cancellationToken);

            await PerformPreEvent(context, currentChangeSetItems, cancellationToken);

            await PerformPersist(context, currentChangeSetItems, cancellationToken);

            context.ChangeSet.Entries.Clear();

            await PerformPostEvent(context, currentChangeSetItems, cancellationToken);

            return context.Result;
        }

        private static string GetAuthorizeFailedMessage(ChangeSetItem item)
        {
            switch (item.Type)
            {
                case ChangeSetItemType.DataModification:
                    DataModificationItem dataModification = (DataModificationItem)item;
                    string message = null;
                    if (dataModification.IsNewRequest)
                    {
                        message = Resources.NoPermissionToInsertEntity;
                    }
                    else if (dataModification.IsUpdateRequest)
                    {
                        message = Resources.NoPermissionToUpdateEntity;
                    }
                    else if (dataModification.IsDeleteRequest)
                    {
                        message = Resources.NoPermissionToDeleteEntity;
                    }
                    else
                    {
                        throw new NotSupportedException(Resources.DataModificationMustBeCUD);
                    }

                    return string.Format(CultureInfo.InvariantCulture, message, dataModification.EntitySetName);

                case ChangeSetItemType.ActionInvocation:
                    ActionInvocationItem actionInvocation = (ActionInvocationItem)item;
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        Resources.NoPermissionToInvokeAction,
                        actionInvocation.ActionName);

                default:
                    throw new InvalidOperationException(string.Format(
                        CultureInfo.InvariantCulture,
                        Resources.InvalidChangeSetEntryType,
                        item.Type));
            }
        }

        private static async Task PerformValidate(
            SubmitContext context,
            IEnumerable<ChangeSetItem> changeSetItems,
            CancellationToken cancellationToken)
        {
            await InvokeAuthorizers(context, changeSetItems, cancellationToken);

            await InvokeValidators(context, changeSetItems, cancellationToken);

            foreach (ChangeSetItem item in changeSetItems.Where(i => i.HasChanged()))
            {
                if (item.ChangeSetItemProcessingStage == ChangeSetItemProcessingStage.ChangedWithinOwnPreEventing)
                {
                    item.ChangeSetItemProcessingStage = ChangeSetItemProcessingStage.PreEvented;
                }
                else
                {
                    item.ChangeSetItemProcessingStage = ChangeSetItemProcessingStage.Validated;
                }
            }
        }

        private static async Task InvokeAuthorizers(
            SubmitContext context,
            IEnumerable<ChangeSetItem> changeSetItems,
            CancellationToken cancellationToken)
        {
            var authorizer = context.GetApiService<IChangeSetItemAuthorizer>();
            if (authorizer == null)
            {
                return;
            }

            foreach (ChangeSetItem item in changeSetItems.Where(i => i.HasChanged()))
            {
                if (!await authorizer.AuthorizeAsync(context, item, cancellationToken))
                {
                    var message = DefaultSubmitHandler.GetAuthorizeFailedMessage(item);
                    throw new SecurityException(message);
                }
            }
        }

        private static async Task InvokeValidators(
            SubmitContext context,
            IEnumerable<ChangeSetItem> changeSetItems,
            CancellationToken cancellationToken)
        {
            var validator = context.GetApiService<IChangeSetItemValidator>();
            if (validator == null)
            {
                return;
            }

            Collection<ChangeSetItemValidationResult> validationResults = new Collection<ChangeSetItemValidationResult>();

            foreach (ChangeSetItem entry in changeSetItems.Where(i => i.HasChanged()))
            {
                await validator.ValidateChangeSetItemAsync(context, entry, validationResults, cancellationToken);
            }

            IEnumerable<ChangeSetItemValidationResult> errors = validationResults.Where(result => result.Severity == EventLevel.Error);

            if (errors.Any())
            {
                string validationErrorMessage = Resources.ValidationFailsTheOperation;
                throw new ChangeSetValidationException(validationErrorMessage)
                {
                    ValidationResults = errors
                };
            }
        }

        private static async Task PerformPreEvent(
            SubmitContext context,
            IEnumerable<ChangeSetItem> changeSetItems,
            CancellationToken cancellationToken)
        {
            foreach (ChangeSetItem entry in changeSetItems)
            {
                if (entry.ChangeSetItemProcessingStage == ChangeSetItemProcessingStage.Validated)
                {
                    entry.ChangeSetItemProcessingStage = ChangeSetItemProcessingStage.PreEventing;

                    var filter = context.GetApiService<IChangeSetItemProcessor>();
                    if (filter != null)
                    {
                        await filter.OnProcessingChangeSetItemAsync(context, entry, cancellationToken);
                    }

                    if (entry.ChangeSetItemProcessingStage == ChangeSetItemProcessingStage.PreEventing)
                    {
                        // if the state is still the intermediate state,
                        // the entity was not changed during processing
                        // and can move to the next step
                        entry.ChangeSetItemProcessingStage = ChangeSetItemProcessingStage.PreEvented;
                    }
                    else if (entry.ChangeSetItemProcessingStage == ChangeSetItemProcessingStage.Changed /*&&
                        entity.Details.EntityState == originalEntityState*/)
                    {
                        entry.ChangeSetItemProcessingStage = ChangeSetItemProcessingStage.ChangedWithinOwnPreEventing;
                    }
                }
            }
        }

        private static async Task PerformPersist(
            SubmitContext context,
            IEnumerable<ChangeSetItem> changeSetItems,
            CancellationToken cancellationToken)
        {
            // Once the change is persisted, the EntityState is lost.
            // In order to invoke the correct post-CUD event, remember which action was performed on the entity.
            foreach (ChangeSetItem item in changeSetItems)
            {
                if (item.Type == ChangeSetItemType.DataModification)
                {
                    DataModificationItem dataModification = (DataModificationItem)item;
                    if (dataModification.IsNewRequest)
                    {
                        dataModification.ChangeSetItemAction = ChangeSetItemAction.Insert;
                    }
                    else if (dataModification.IsUpdateRequest)
                    {
                        dataModification.ChangeSetItemAction = ChangeSetItemAction.Update;
                    }
                    else if (dataModification.IsDeleteRequest)
                    {
                        dataModification.ChangeSetItemAction = ChangeSetItemAction.Remove;
                    }
                }
            }

            var executor = context.GetApiService<ISubmitExecutor>();
            if (executor == null)
            {
                throw new NotSupportedException(Resources.SubmitExecutorMissing);
            }

            context.Result = await executor.ExecuteSubmitAsync(context, cancellationToken);
        }

        private static async Task PerformPostEvent(
            SubmitContext context,
            IEnumerable<ChangeSetItem> changeSetItems,
            CancellationToken cancellationToken)
        {
            foreach (ChangeSetItem entry in changeSetItems)
            {
                var filter = context.GetApiService<IChangeSetItemProcessor>();
                if (filter != null)
                {
                    await filter.OnProcessedChangeSetItemAsync(context, entry, cancellationToken);
                }
            }
        }
    }
}
