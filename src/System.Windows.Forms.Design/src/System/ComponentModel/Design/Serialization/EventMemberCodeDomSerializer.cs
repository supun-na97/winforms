﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.CodeDom;
using System.Diagnostics;
using System.Reflection;

namespace System.ComponentModel.Design.Serialization
{
    /// <summary>
    ///  A MemberCodeDomSerializer for events.
    /// </summary>
    internal sealed class EventMemberCodeDomSerializer : MemberCodeDomSerializer
    {
        private static readonly CodeThisReferenceExpression _thisRef = new CodeThisReferenceExpression();
        private static EventMemberCodeDomSerializer s_default;

        internal static EventMemberCodeDomSerializer Default
        {
            get
            {
                s_default ??= new EventMemberCodeDomSerializer();

                return s_default;
            }
        }

        /// <summary>
        ///  This method actually performs the serialization.  When the member is serialized
        ///  the necessary statements will be added to the statements collection.
        /// </summary>
        public override void Serialize(IDesignerSerializationManager manager, object value, MemberDescriptor descriptor, CodeStatementCollection statements)
        {
            ArgumentNullException.ThrowIfNull(manager);
            ArgumentNullException.ThrowIfNull(value);

            if (!(descriptor is EventDescriptor eventToSerialize))
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            ArgumentNullException.ThrowIfNull(statements);

            try
            {
                // If the IEventBindingService is not available, we don't throw - we just don't do anything.
                if (manager.GetService(typeof(IEventBindingService)) is IEventBindingService eventBindings)
                {
                    PropertyDescriptor prop = eventBindings.GetEventProperty(eventToSerialize);
                    string methodName = (string)prop.GetValue(value);

                    if (methodName is not null)
                    {
                        Trace(TraceLevel.Verbose, $"Event {eventToSerialize.Name} bound to {methodName}");
                        CodeExpression eventTarget = SerializeToExpression(manager, value);
                        TraceIf(TraceLevel.Warning, eventTarget is null, $"Object has no name and no property ref in context so we cannot serialize events: {value}");
                        if (eventTarget is not null)
                        {
                            CodeTypeReference delegateTypeRef = new CodeTypeReference(eventToSerialize.EventType);
                            CodeDelegateCreateExpression delegateCreate = new CodeDelegateCreateExpression(delegateTypeRef, _thisRef, methodName);
                            CodeEventReferenceExpression eventRef = new CodeEventReferenceExpression(eventTarget, eventToSerialize.Name);
                            CodeAttachEventStatement attach = new CodeAttachEventStatement(eventRef, delegateCreate);

                            attach.UserData[typeof(Delegate)] = eventToSerialize.EventType;
                            statements.Add(attach);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                // Since we usually go through reflection, don't
                // show what our engine does, show what caused
                // the problem.
                //
                if (e is TargetInvocationException)
                {
                    e = e.InnerException;
                }

                manager.ReportError(new CodeDomSerializerException(string.Format(SR.SerializerPropertyGenFailed, eventToSerialize.Name, e.Message), manager));
            }
        }

        /// <summary>
        ///  This method returns true if the given member descriptor should be serialized,
        ///  or false if there is no need to serialize the member.
        /// </summary>
        public override bool ShouldSerialize(IDesignerSerializationManager manager, object value, MemberDescriptor descriptor) => true;
    }
}
