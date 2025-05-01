// FarNet plugin for Far Manager
// Copyright (c) Roman Kuzmin

using System;
using System.Xml.Serialization;
using FarNet;

namespace FarNet.PCloud;
#pragma warning disable 1591

/// <summary>
/// Settings wrapper for ACD module using XML serialization.
/// </summary>
public sealed class ACDSettings : ModuleSettings<ACDSettings.Data>
{
	public static ACDSettings Default { get; } = new ACDSettings();

	ACDSettings()
		: base(Far.Api.GetFolderPath(SpecialFolder.RoamingData) + @"\FarNet\ACD.Settings.xml")
	{ }

	public class Data
	{
		public string? ClientId { get; set; }

		public string? ClientSecret { get; set; }

		public string? AuthToken { get; set; }
	}
}
