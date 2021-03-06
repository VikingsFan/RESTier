﻿// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using Microsoft.Restier.Core;

namespace Microsoft.Restier.WebApi.Model
{
    /// <summary>
    /// Offers a collection of extension methods to <see cref="ApiConfiguration"/>.
    /// </summary>
    public static class ApiConfigurationExtensions
    {
        private const string IgnoredPropertiesKey = "Microsoft.Restier.WebApi.IgnoredProperties";

        #region IgnoreProperty

        /// <summary>
        /// Ignores the given property in ApiBase or sub-class when building the model.
        /// </summary>
        /// <param name="configuration">An API configuration.</param>
        /// <param name="propertyName">The name of the property to be ignored.</param>
        /// <returns>The current API configuration instance.</returns>
        public static ApiConfiguration IgnoreProperty(
            this ApiConfiguration configuration,
            string propertyName)
        {
            Ensure.NotNull(configuration, "configuration");
            Ensure.NotNull(propertyName, "propertyName");

            configuration.GetIgnoredPropertiesImplementation().Add(propertyName);
            return configuration;
        }

        #endregion

        #region IgnoreProperty Internal

        internal static bool IsPropertyIgnored(this ApiConfiguration configuration, string propertyName)
        {
            Ensure.NotNull(configuration, "configuration");

            return configuration.GetIgnoredPropertiesImplementation().Contains(propertyName);
        }

        #endregion

        #region IgnoreProperty Private

        private static ICollection<string> GetIgnoredPropertiesImplementation(this ApiConfiguration configuration)
        {
            var ignoredProperties = configuration.GetProperty<ICollection<string>>(IgnoredPropertiesKey);
            if (ignoredProperties == null)
            {
                ignoredProperties = new HashSet<string>();
                configuration.SetProperty(IgnoredPropertiesKey, ignoredProperties);
            }

            return ignoredProperties;
        }

        #endregion
    }
}