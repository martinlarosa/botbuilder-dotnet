﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Dynamic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Bot.Builder.Dialogs.Choices;
using Microsoft.Bot.Schema;
using Newtonsoft.Json;

namespace Microsoft.Bot.Builder.Dialogs
{
    /// <summary>
    /// Basic configuration options supported by all prompts.
    /// </summary>
    /// <typeparam name="T">The type of the <see cref="Prompt{T}"/>.</typeparam>
    public abstract class Prompt<T> : Dialog
    {
        private const string PersistedOptions = "options";
        private const string PersistedState = "state";

        protected PromptValidator<T> _validator = null;

        public Prompt(string dialogId = null, PromptValidator<T> validator = null)
            : base(dialogId)
        {
            _validator = validator;
        }

        public override async Task<DialogTurnResult> BeginDialogAsync(DialogContext dc, object options, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (dc == null)
            {
                throw new ArgumentNullException(nameof(dc));
            }

            var promptOptions = (PromptOptions)options;

            // Ensure prompts have input hint set
            if (promptOptions.Prompt != null && string.IsNullOrEmpty(promptOptions.Prompt.InputHint))
            {
                promptOptions.Prompt.InputHint = InputHints.ExpectingInput;
            }

            if (promptOptions.RetryPrompt != null && string.IsNullOrEmpty(promptOptions.RetryPrompt.InputHint))
            {
                promptOptions.RetryPrompt.InputHint = InputHints.ExpectingInput;
            }

            // Initialize prompt state
            var state = dc.DialogState;
            state[PersistedOptions] = promptOptions;
            state[PersistedState] = new Dictionary<string, object>();

            if (!string.IsNullOrEmpty(Property))
            {
                var tokens = dc.State.Query(Property);
                if (tokens.Any())
                {
                    if (dc.State.TryGetValue<T>(Property, out var value))
                    {
                        // if we have the value and it's valid, then EndDialog with the value
                        if (_validator != null)
                        {
                            var promptContext = new PromptValidatorContext<T>(dc.Context, new PromptRecognizerResult<T>() { Succeeded = true, Value = value }, state, promptOptions);
                            var isValid = await _validator(promptContext, cancellationToken).ConfigureAwait(false);
                            if (isValid)
                            {
                                return await dc.EndDialogAsync(value).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            // no validator, so it's valid
                            return await dc.EndDialogAsync(value).ConfigureAwait(false);
                        }
                    }
                }
            }

            // Send initial prompt
            await OnPromptAsync(dc.Context, (IDictionary<string, object>)state[PersistedState], (PromptOptions)state[PersistedOptions], false, cancellationToken).ConfigureAwait(false);

            return Dialog.EndOfTurn;
        }

        public override async Task<DialogConsultation> ConsultDialogAsync(DialogContext dc, CancellationToken cancellationToken = default(CancellationToken))
        {
            // Don't do anything for non-message activities
            if (dc.Context.Activity.Type != ActivityTypes.Message)
            {
                return new DialogConsultation()
                {
                    Desire = DialogConsultationDesire.CanProcess,
                    Processor = async (dialogContext2) => Dialog.EndOfTurn,
                };
            }

            // Perform base recognition
            var state = dc.DialogState;
            var recognized = await this.OnRecognizeAsync(dc.Context, (IDictionary<string, object>)state[PersistedState], (PromptOptions)state[PersistedOptions]).ConfigureAwait(false);

            return new DialogConsultation()
            {
                Desire = recognized.Succeeded && !recognized.AllowInterruption ? DialogConsultationDesire.ShouldProcess : DialogConsultationDesire.CanProcess,
                Processor = async (dialogContext) =>
                {
                    // Validate the return value
                    bool isValid = false;
                    if (this._validator != null)
                    {
                        isValid = await this._validator(new PromptValidatorContext<T>(dialogContext.Context, recognized, (IDictionary<string, object>)state[PersistedState], (PromptOptions)state[PersistedOptions]), cancellationToken).ConfigureAwait(false);
                    }
                    else if (recognized.Succeeded)
                    {
                        isValid = true;
                    }

                    // Return recognized value or re-prompt
                    if (isValid)
                    {
                        return await dc.EndDialogAsync(recognized.Value, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        if (!dc.Context.Responded)
                        {
                            await this.OnPromptAsync(dc.Context, (IDictionary<string, object>)state[PersistedState], (PromptOptions)state[PersistedOptions], true).ConfigureAwait(false);
                        }

                        return Dialog.EndOfTurn;
                    }
                },
            };
        }


        public override async Task<DialogTurnResult> ResumeDialogAsync(DialogContext dc, DialogReason reason, object result = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (result is CancellationToken)
            {
                throw new ArgumentException($"{nameof(result)} cannot be a cancellation token");
            }

            // Prompts are typically leaf nodes on the stack but the dev is free to push other dialogs
            // on top of the stack which will result in the prompt receiving an unexpected call to
            // dialogResume() when the pushed on dialog ends.
            // To avoid the prompt prematurely ending we need to implement this method and
            // simply re-prompt the user.
            await RepromptDialogAsync(dc.Context, dc.ActiveDialog).ConfigureAwait(false);
            return Dialog.EndOfTurn;
        }

        public override async Task RepromptDialogAsync(ITurnContext turnContext, DialogInstance instance, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = (IDictionary<string, object>)((Dictionary<string, object>)instance.State)[PersistedState];
            var options = (PromptOptions)((Dictionary<string, object>)instance.State)[PersistedOptions];
            await OnPromptAsync(turnContext, state, options, isRetry: true).ConfigureAwait(false);
        }

        protected abstract Task OnPromptAsync(ITurnContext turnContext, IDictionary<string, object> state, PromptOptions options, bool isRetry, CancellationToken cancellationToken = default(CancellationToken));

        protected abstract Task<PromptRecognizerResult<T>> OnRecognizeAsync(ITurnContext turnContext, IDictionary<string, object> state, PromptOptions options, CancellationToken cancellationToken = default(CancellationToken));

        protected IMessageActivity AppendChoices(IMessageActivity prompt, string channelId, IList<Choice> choices, ListStyle style, ChoiceFactoryOptions options = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            // Get base prompt text (if any)
            var text = prompt != null && !string.IsNullOrEmpty(prompt.Text) ? prompt.Text : string.Empty;

            // Create temporary msg
            IMessageActivity msg;
            switch (style)
            {
                case ListStyle.Inline:
                    msg = ChoiceFactory.Inline(choices, text, null, options);
                    break;

                case ListStyle.List:
                    msg = ChoiceFactory.List(choices, text, null, options);
                    break;

                case ListStyle.SuggestedAction:
                    msg = ChoiceFactory.SuggestedAction(choices, text);
                    break;

                case ListStyle.HeroCard:
                    msg = ChoiceFactory.HeroCard(choices, text);
                    break;

                case ListStyle.None:
                    msg = Activity.CreateMessageActivity();
                    msg.Text = text;
                    break;

                default:
                    msg = ChoiceFactory.ForChannel(channelId, choices, text, null, options);
                    break;
            }

            // Update prompt with text, actions and attachments
            if (prompt != null)
            {
                // clone the prompt the set in the options (note ActivityEx has Properties so this is the safest mechanism)
                prompt = JsonConvert.DeserializeObject<Activity>(JsonConvert.SerializeObject(prompt));

                prompt.Text = msg.Text;

                if (msg.SuggestedActions != null && msg.SuggestedActions.Actions != null && msg.SuggestedActions.Actions.Count > 0)
                {
                    prompt.SuggestedActions = msg.SuggestedActions;
                }

                if (msg.Attachments != null && msg.Attachments.Any())
                {
                    prompt.Attachments = msg.Attachments;
                }

                return prompt;
            }
            else
            {
                msg.InputHint = InputHints.ExpectingInput;
                return msg;
            }
        }
    }
}