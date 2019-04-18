﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Bot.Builder.Expressions.Parser;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Microsoft.Bot.Builder.Dialogs
{
    /// <summary>
    /// Defines the shape of the state object returned by calling DialogContext.State.ToJson()
    /// </summary>
    public class DialogContextVisibleState
    {
        [JsonProperty(PropertyName = "user")]
        public Dictionary<string, object> User { get; set; }

        [JsonProperty(PropertyName = "conversation")]
        public Dictionary<string, object> Conversation { get; set; }

        [JsonProperty(PropertyName = "dialog")]
        public Dictionary<string, object> Dialog { get; set; }
    }

    public class DialogContextState : IDictionary<string, object>
    {
        private static JsonSerializerSettings expressionCaseSettings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() },
            NullValueHandling = NullValueHandling.Ignore,
        };

        private readonly DialogContext dialogContext;

        public DialogContextState(DialogContext dc, Dictionary<string, object> settings, Dictionary<string, object> userState, Dictionary<string, object> conversationState, Dictionary<string, object> turnState)
        {
            this.dialogContext = dc ?? throw new ArgumentNullException(nameof(dc));
            this.Settings = settings;
            this.User = userState;
            this.Conversation = conversationState;
            this.Turn = turnState;
        }

        /// <summary>
        /// Gets or sets settings for the application.
        /// </summary>
        [JsonProperty(PropertyName = "settings")]
        public Dictionary<string, object> Settings { get; set; }

        /// <summary>
        /// Gets or sets state associated with the active user in the turn.
        /// </summary>
        [JsonProperty(PropertyName = "user")]
        public Dictionary<string, object> User { get; set; }

        /// <summary>
        /// Gets or sets state assocaited with the active conversation for the turn.
        /// </summary>
        [JsonProperty(PropertyName = "conversation")]
        public Dictionary<string, object> Conversation { get; set; }

        /// <summary>
        /// Gets or sets state associated with the active dialog for the turn.
        /// </summary>
        [JsonProperty(PropertyName = "dialog")]
        public Dictionary<string, object> Dialog
        {
            get
            {
                var instance = dialogContext.ActiveDialog;

                if (instance == null)
                {
                    if (dialogContext.Parent != null)
                    {
                        instance = dialogContext.Parent.ActiveDialog;
                    }
                    else
                    {
                        return null;  //throw new Exception("DialogContext.State.Dialog: no active or parent dialog instance.");
                    }
                }

                return (Dictionary<string, object>)instance.State;
            }

            set
            {
                var instance = dialogContext.ActiveDialog;

                if (instance == null)
                {
                    if (dialogContext.Parent != null)
                    {
                        instance = dialogContext.Parent.ActiveDialog;
                    }
                    else
                    {
                        throw new Exception("DialogContext.State.Dialog: no active or parent dialog instance.");
                    }
                }

                instance.State = value;

            }
        }

        /// <summary>
        /// Gets or sets state associated with the current turn only (this is non-persisted).
        /// </summary>
        [JsonProperty(PropertyName = "turn")]
        public Dictionary<string, object> Turn { get; set; }

        public ICollection<string> Keys => new[] { "user", "conversation", "dialog", "turn", "settings" };

        public ICollection<object> Values => new[] { User, Conversation, Dialog, Turn };

        public int Count => 3;

        public bool IsReadOnly => true;

        public object this[string key]
        {
            get
            {
                if (TryGetValue(key, out object result))
                {
                    return result;
                }

                return null;
            }

            set
            {
                System.Diagnostics.Trace.TraceError("DialogContextState doesn't support adding/changinge the base properties");
            }
        }

        public DialogContextVisibleState ToJson()
        {
            var instance = dialogContext.ActiveDialog;

            if (instance == null)
            {
                if (dialogContext.Parent != null)
                {
                    instance = dialogContext.Parent.ActiveDialog;
                }
            }

            return new DialogContextVisibleState()
            {
                Conversation = this.Conversation,
                User = this.User,
                Dialog = (Dictionary<string, object>)instance?.State,
            };
        }

        public IEnumerable<JToken> Query(string pathExpression)
        {
            JToken json = JToken.FromObject(this);

            return json.SelectTokens(pathExpression);
        }

        public T GetValue<T>(string pathExpression, T defaultValue = default(T))
        {
            return GetValue<T>(this, pathExpression, defaultValue);
        }

        public T GetValue<T>(object o, string pathExpression, T defaultValue = default(T))
        {
            JToken result = null;
            if (pathExpression.StartsWith("$"))
            {
                // jpath
                if (o != null && o.GetType() == typeof(JArray))
                {
                    int index = 0;
                    if (int.TryParse(pathExpression, out index) && index < JArray.FromObject(o).Count)
                    {
                        result = JArray.FromObject(o)[index];
                    }
                }
                else if (o != null && o is JObject)
                {
                    result = ((JObject)o).SelectToken(pathExpression);
                }
                else
                {
                    result = JToken.FromObject(o).SelectToken(pathExpression);
                }
            }
            else
            {
                // normal expression
                var exp = new ExpressionEngine().Parse(pathExpression);
                var (value, error) = exp.TryEvaluate(o);
                if (value is JToken)
                {
                    result = (JToken)value;
                }
                else if (value != null)
                {
                    return (T)value;
                }
            }

            if (result != null)
            {
                return result.ToObject<T>();
            }
            else
            {
                return defaultValue;
            }
        }

        public bool HasValue<T>(string pathExpression)
        {
            return HasValue<T>(this, pathExpression);
        }

        public bool HasValue<T>(object o, string pathExpression)
        {
            JToken result = null;
            if (o != null && o.GetType() == typeof(JArray))
            {
                int index = 0;
                if (int.TryParse(pathExpression, out index) && index < JArray.FromObject(o).Count)
                {
                    result = JArray.FromObject(o)[index];
                }
            }
            else
            {
                result = JToken.FromObject(o).SelectToken(pathExpression);
            }

            if (result != null)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        public void SetValue(string pathExpression, object value)
        {
            if (value is Task)
            {
                throw new Exception($"{pathExpression} = You can't pass an unresolved Task to SetValue");
            }

            // If the json path does not exist
            string[] segments = pathExpression.Split('.').Select(segment => segment.ToLower()).ToArray();
            dynamic current = this;

            for (int i = 0; i < segments.Length - 1; i++)
            {
                var segment = segments[i];
                if (current is IDictionary<string, object> curDict)
                {
                    if (!curDict.ContainsKey(segment))
                    {
                        curDict[segment] = new JObject();
                    }

                    current = curDict[segment];
                }
            }

            if (value is JToken || value is JObject || value is JArray)
            {
                current[segments.Last()] = (JToken)value;
            }
            else if (value == null)
            {
                current[segments.Last()] = null;
            }
            else if (value is string || value is byte || value is bool ||
                    value is Int16 || value is Int32 || value is Int64 ||
                    value is UInt16 || value is UInt32 || value is UInt64 ||
                    value is Decimal || value is float || value is double)
            {
                current[segments.Last()] = JValue.FromObject(value);
            }
            else
            {
                current[segments.Last()] = (JObject)JsonConvert.DeserializeObject(JsonConvert.SerializeObject(value, expressionCaseSettings));
            }
        }

        public void Add(string key, object value)
        {
            throw new NotImplementedException();
        }

        public bool ContainsKey(string key)
        {
            return this.Keys.Contains(key.ToLower());
        }

        public bool Remove(string key)
        {
            throw new NotImplementedException();
        }

        public bool TryGetValue(string key, out object value)
        {
            value = null;
            switch (key.ToLower())
            {
                case "user":
                    value = this.User;
                    return true;
                case "conversation":
                    value = this.Conversation;
                    return true;
                case "dialog":
                    value = this.Dialog;
                    return true;
                case "settings":
                    value = this.Settings;
                    return true;
                case "turn":
                    value = this.Turn;
                    return true;
            }

            return false;
        }

        public void Add(KeyValuePair<string, object> item)
        {
            throw new NotImplementedException();
        }

        public void Clear()
        {
            throw new NotImplementedException();
        }

        public bool Contains(KeyValuePair<string, object> item)
        {
            throw new NotImplementedException();
        }

        public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex)
        {
            throw new NotImplementedException();
        }

        public bool Remove(KeyValuePair<string, object> item)
        {
            throw new NotImplementedException();
        }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            yield return new KeyValuePair<string, object>("user", this.User);
            yield return new KeyValuePair<string, object>("conversation", this.Conversation);
            yield return new KeyValuePair<string, object>("dialog", this.Dialog);
            yield return new KeyValuePair<string, object>("settings", this.Settings);
            yield return new KeyValuePair<string, object>("turn", this.Turn);
            yield break;
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            throw new NotImplementedException();
        }

    }
}
