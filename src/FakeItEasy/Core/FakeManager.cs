namespace FakeItEasy.Core
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Reflection;
    using FakeItEasy.Configuration;

    /// <summary>
    /// The central point in the API for proxied fake objects handles interception
    /// of fake object calls by using a set of rules. User defined rules can be inserted
    /// by using the AddRule-method.
    /// </summary>
    [Serializable]
    public partial class FakeManager : IFakeCallProcessor
    {
        private readonly CallRuleMetadata[] postUserRules;
        private readonly CallRuleMetadata[] preUserRules;
        private readonly ConcurrentQueue<ICompletedFakeObjectCall> recordedCalls;
        private readonly LinkedList<IInterceptionListener> interceptionListeners;
        private readonly WeakReference objectReference;

        /// <summary>
        /// Initializes a new instance of the <see cref="FakeManager"/> class.
        /// </summary>
        /// <param name="fakeObjectType">The faked type.</param>
        /// <param name="proxy">The faked proxy object.</param>
        internal FakeManager(Type fakeObjectType, object proxy)
        {
            Guard.AgainstNull(fakeObjectType, nameof(fakeObjectType));
            Guard.AgainstNull(proxy, nameof(proxy));

            this.objectReference = new WeakReference(proxy);
            this.FakeObjectType = fakeObjectType;

            this.preUserRules = new[]
                                    {
                                        new CallRuleMetadata { Rule = new EventRule(this) }
                                    };
            this.AllUserRules = new LinkedList<CallRuleMetadata>();
            this.postUserRules = new[]
                                     {
                                         new CallRuleMetadata { Rule = new ObjectMemberRule(this) },
                                         new CallRuleMetadata { Rule = new AutoFakePropertyRule(this) },
                                         new CallRuleMetadata { Rule = new PropertySetterRule(this) },
                                         new CallRuleMetadata { Rule = new DefaultReturnValueRule() }
                                     };

            this.recordedCalls = new ConcurrentQueue<ICompletedFakeObjectCall>();
            this.interceptionListeners = new LinkedList<IInterceptionListener>();
        }

        /// <summary>
        /// A delegate responsible for creating FakeObject instances.
        /// </summary>
        /// <param name="fakeObjectType">The faked type.</param>
        /// <param name="proxy">The faked proxy object.</param>
        /// <returns>An instance of <see cref="FakeManager"/>.</returns>
        internal delegate FakeManager Factory(Type fakeObjectType, object proxy);

        /// <summary>
        /// Gets the faked proxy object.
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1716:IdentifiersShouldNotMatchKeywords", MessageId = "Object", Justification = "Renaming this property would be a breaking change.")]
        public virtual object Object => this.objectReference.Target;

        /// <summary>
        /// Gets the faked type.
        /// </summary>
        public virtual Type FakeObjectType { get; }

        /// <summary>
        /// Gets the interceptions that are currently registered with the fake object.
        /// </summary>
        public virtual IEnumerable<IFakeObjectCallRule> Rules
        {
            get { return this.AllUserRules.Select(x => x.Rule); }
        }

        internal LinkedList<CallRuleMetadata> AllUserRules { get; }

        private IEnumerable<CallRuleMetadata> AllRules =>
            this.preUserRules.Concat(this.AllUserRules.Concat(this.postUserRules));

        /// <summary>
        /// Adds a call rule to the fake object.
        /// </summary>
        /// <param name="rule">The rule to add.</param>
        public virtual void AddRuleFirst(IFakeObjectCallRule rule)
        {
            this.AllUserRules.AddFirst(new CallRuleMetadata { Rule = rule });
        }

        /// <summary>
        /// Adds a call rule last in the list of user rules, meaning it has the lowest priority possible.
        /// </summary>
        /// <param name="rule">The rule to add.</param>
        public virtual void AddRuleLast(IFakeObjectCallRule rule)
        {
            this.AllUserRules.AddLast(new CallRuleMetadata { Rule = rule });
        }

        /// <summary>
        /// Removes the specified rule for the fake object.
        /// </summary>
        /// <param name="rule">The rule to remove.</param>
        public virtual void RemoveRule(IFakeObjectCallRule rule)
        {
            Guard.AgainstNull(rule, nameof(rule));

            var ruleToRemove = this.AllUserRules.FirstOrDefault(x => x.Rule.Equals(rule));
            this.AllUserRules.Remove(ruleToRemove);
        }

        /// <summary>
        /// Adds an interception listener to the manager.
        /// </summary>
        /// <param name="listener">The listener to add.</param>
        public void AddInterceptionListener(IInterceptionListener listener)
        {
            this.interceptionListeners.AddFirst(listener);
        }

        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes",
            Justification = "Explicit implementation to be able to make the IFakeCallProcessor interface internal.")]
        void IFakeCallProcessor.Process(IInterceptedFakeObjectCall fakeObjectCall)
        {
            foreach (var listener in this.interceptionListeners)
            {
                listener.OnBeforeCallIntercepted(fakeObjectCall);
            }

            var ruleToUse =
                (from rule in this.AllRules
                 where rule.Rule.IsApplicableTo(fakeObjectCall) && rule.HasNotBeenCalledSpecifiedNumberOfTimes()
                 select rule).First();

            try
            {
                ApplyRule(ruleToUse, fakeObjectCall);
            }
            finally
            {
                var readonlyCall = fakeObjectCall.AsReadOnly();

                this.RecordCall(readonlyCall);

                foreach (var listener in this.interceptionListeners.Reverse())
                {
                    listener.OnAfterCallIntercepted(readonlyCall, ruleToUse.Rule);
                }
            }
        }

        /// <summary>
        /// Returns a list of all calls on the managed object.
        /// </summary>
        /// <returns>A list of all calls on the managed object.</returns>
        internal IEnumerable<ICompletedFakeObjectCall> GetRecordedCalls()
        {
            return this.recordedCalls;
        }

        /// <summary>
        /// Records that a call has occurred on the managed object.
        /// </summary>
        /// <param name="call">The call to remember.</param>
        internal void RecordCall(ICompletedFakeObjectCall call)
        {
            this.recordedCalls.Enqueue(call);
        }

        /// <summary>
        /// Removes any specified user rules.
        /// </summary>
        internal virtual void ClearUserRules()
        {
            this.AllUserRules.Clear();
        }

        private static void ApplyRule(CallRuleMetadata rule, IInterceptedFakeObjectCall fakeObjectCall)
        {
            rule.CalledNumberOfTimes++;
            rule.Rule.Apply(fakeObjectCall);
        }

        private void MoveRuleToFront(CallRuleMetadata rule)
        {
            if (this.AllUserRules.Remove(rule))
            {
                this.AllUserRules.AddFirst(rule);
            }
        }

        private void MoveRuleToFront(IFakeObjectCallRule rule)
        {
            var metadata = this.AllRules.Single(x => x.Rule.Equals(rule));
            this.MoveRuleToFront(metadata);
        }
    }
}
