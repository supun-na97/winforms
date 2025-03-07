﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Reflection;
using System.Runtime.CompilerServices;

namespace Xunit;

/// <summary>
///  Generates <see cref="TheoryAttribute"/> data for an invalid enum value.
/// </summary>
/// <typeparam name="TEnum"></typeparam>
public class InvalidEnumDataAttribute<TEnum> : CommonMemberDataAttribute where TEnum : unmanaged, Enum
{
    private static readonly TheoryData<TEnum> _data = new();

    public unsafe InvalidEnumDataAttribute()
        : base(typeof(InvalidEnumDataAttribute<TEnum>), nameof(GetTheoryData))
    {
        ulong maxValue = ulong.MaxValue >>> ((sizeof(ulong) - sizeof(TEnum)) * 8);
        TEnum currentValue = default;
        bool defined;

        if (typeof(TEnum).GetCustomAttribute<FlagsAttribute>() is not null)
        {
            // Bit flags, pull the first flag from the top.
            ulong currentFlagValue = 1ul << (sizeof(TEnum) * 8) - 1;
            do
            {
                currentValue = Unsafe.As<ulong, TEnum>(ref currentFlagValue);
                defined = Enum.IsDefined(currentValue);
                currentFlagValue >>>= 1;
            }
            while (defined && currentFlagValue > 0);

            if (defined)
            {
                throw new InvalidOperationException("Enum has all flags defined");
            }

            _data.Add(currentValue);
            return;
        }

        // Not a flags enum, add the smallest and largest undefined value.
        ulong minValue = 0;

        do
        {
            currentValue = Unsafe.As<ulong, TEnum>(ref minValue);
            defined = Enum.IsDefined(currentValue);
            minValue++;
        }
        while (defined);

        _data.Add(currentValue);

        do
        {
            currentValue = Unsafe.As<ulong, TEnum>(ref maxValue);
            defined = Enum.IsDefined(currentValue);
            maxValue--;
        }
        while (defined);

        _data.Add(currentValue);
    }

    public unsafe static TheoryData<TEnum> GetTheoryData() => _data;
}
