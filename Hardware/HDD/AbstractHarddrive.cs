﻿/*
 
  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.
 
  Copyright (C) 2009-2012 Michael Möller <mmoeller@openhardwaremonitor.org>
	Copyright (C) 2010 Paul Werelds
  Copyright (C) 2011 Roland Reinl <roland-reinl@gmx.de>
	
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OpenHardwareMonitor.Collections;

namespace OpenHardwareMonitor.Hardware.HDD {
  internal abstract class AbstractHarddrive : Hardware {

    private const int UPDATE_DIVIDER = 30; // update only every 30s

    // array of all harddrive types, matching type is searched in this order
    private static Type[] hddTypes = {       
      typeof(SSDPlextor),
      typeof(SSDIntel),
      typeof(SSDSandforce),
      typeof(SSDIndilinx),
      typeof(SSDSamsung),
      typeof(GenericHarddisk)
    };

    private string firmwareRevision;
    private readonly ISmart smart;

    private readonly IntPtr handle;
    private readonly int index;
    private int count;

    private IList<SmartAttribute> smartAttributes;
    private IDictionary<SmartAttribute, Sensor> sensors;

    protected AbstractHarddrive(ISmart smart, string name, 
      string firmwareRevision, int index, 
      IEnumerable<SmartAttribute> smartAttributes, ISettings settings) 
      : base(name, new Identifier("hdd",
        index.ToString(CultureInfo.InvariantCulture)), settings)
    {
      this.firmwareRevision = firmwareRevision;
      this.smart = smart;
      handle = smart.OpenDrive(index);

      smart.EnableSmart(handle, index);

      this.index = index;
      this.count = 0;

      this.smartAttributes = new List<SmartAttribute>(smartAttributes);

      CreateSensors();
    }

    public static AbstractHarddrive CreateInstance(ISmart smart, 
      int driveIndex, ISettings settings) 
    {
      IntPtr deviceHandle = smart.OpenDrive(driveIndex);

      if (deviceHandle == smart.InvalidHandle) 
        return null;

      string name;
      string firmwareRevision;
      bool nameValid = smart.ReadNameAndFirmwareRevision(deviceHandle, 
        driveIndex, out name, out firmwareRevision);
      bool smartEnabled = smart.EnableSmart(deviceHandle, driveIndex);

      DriveAttributeValue[] values = {};
      if (smartEnabled)
        values = smart.ReadSmartData(deviceHandle, driveIndex);

      smart.CloseHandle(deviceHandle);

      if (!nameValid || string.IsNullOrEmpty(name)) 
        return null;

      foreach (Type type in hddTypes) {
        // get the array of name prefixes for the current type
        NamePrefixAttribute[] namePrefixes = type.GetCustomAttributes(
          typeof(NamePrefixAttribute), true) as NamePrefixAttribute[];

        // get the array of the required SMART attributes for the current type
        RequireSmartAttribute[] requiredAttributes = type.GetCustomAttributes(
          typeof(RequireSmartAttribute), true) as RequireSmartAttribute[];

        // check if all required attributes are present
        bool allRequiredAttributesFound = true;
        foreach (var requireAttribute in requiredAttributes) {
          bool adttributeFound = false;
          foreach (DriveAttributeValue value in values) {
            if (value.Identifier == requireAttribute.AttributeId) {
              adttributeFound = true;
              break;
            }
          }
          if (!adttributeFound) {
            allRequiredAttributesFound = false;
            break;
          }
        }

        // if an attribute is missing, then try the next type
        if (!allRequiredAttributesFound)
          continue;        

        // check if there is a matching name prefix for this type
        foreach (NamePrefixAttribute prefix in namePrefixes) {
          if (name.StartsWith(prefix.Prefix, StringComparison.InvariantCulture)) 
            return Activator.CreateInstance(type, smart, name, firmwareRevision,
              driveIndex, settings) as AbstractHarddrive;
        }
      }

      // no matching type has been found
      return null;
    }

    private void CreateSensors() {
      sensors = new Dictionary<SmartAttribute, Sensor>();

      IList<Pair<SensorType, int>> sensorTypeAndChannels = 
        new List<Pair<SensorType, int>>();

      DriveAttributeValue[] values = smart.ReadSmartData(handle, index);

      foreach (SmartAttribute attribute in smartAttributes) {
        if (!attribute.SensorType.HasValue) 
          continue;

        bool found = false;
        foreach (DriveAttributeValue value in values) {
          if (value.Identifier == attribute.Identifier) {
            found = true;
            break;
          }
        }
        if (!found)
          continue;

        Pair<SensorType, int> pair = new Pair<SensorType, int>(
          attribute.SensorType.Value, attribute.SensorChannel);

        if (!sensorTypeAndChannels.Contains(pair)) {
          Sensor sensor = new Sensor(attribute.Name, 
            attribute.SensorChannel, attribute.DefaultHiddenSensor, 
            attribute.SensorType.Value, this, null, settings);

          sensors.Add(attribute, sensor);
          ActivateSensor(sensor);
          sensorTypeAndChannels.Add(pair);
        }     
      }
    }

    public override HardwareType HardwareType {
      get { return HardwareType.HDD; }
    }

    public virtual void UpdateAdditionalSensors(DriveAttributeValue[] values) {}

    public override void Update() {
      if (count == 0) {
        DriveAttributeValue[] values = smart.ReadSmartData(handle, index);

        foreach (KeyValuePair<SmartAttribute, Sensor> keyValuePair in sensors) {
          SmartAttribute attribute = keyValuePair.Key;          
          foreach (DriveAttributeValue value in values) {
            if (value.Identifier == attribute.Identifier) {
              Sensor sensor = keyValuePair.Value;
              sensor.Value = attribute.ConvertValue(value);
            }
          }
        }

        UpdateAdditionalSensors(values);
      }

      count++; 
      count %= UPDATE_DIVIDER; 
    }

    public override string GetReport() {
      StringBuilder r = new StringBuilder();
      DriveAttributeValue[] values = smart.ReadSmartData(handle, index);
      DriveThresholdValue[] thresholds = 
        smart.ReadSmartThresholds(handle, index);

      if (values.Length > 0) {
        r.AppendLine(this.GetType().Name);
        r.AppendLine();
        r.AppendLine("Drive name: " + name);
        r.AppendLine("Firmware version: " + firmwareRevision);
        r.AppendLine();
        r.AppendFormat(CultureInfo.InvariantCulture, 
          " {0}{1}{2}{3}{4}{5}{6}{7}",
          ("ID").PadRight(3),
          ("Description").PadRight(35),
          ("Raw Value").PadRight(13),
          ("Worst").PadRight(6),
          ("Value").PadRight(6),
          ("Thres").PadRight(6),
          ("Physical").PadRight(8),
          Environment.NewLine);

        foreach (DriveAttributeValue value in values) {
          if (value.Identifier == 0x00) 
            break;

          byte? threshold = null;
          foreach (DriveThresholdValue t in thresholds) {
            if (t.Identifier == value.Identifier) {
              threshold = t.Threshold;
            }
          }

          string description = "Unknown";
          float? physical = null;
          foreach (SmartAttribute a in smartAttributes) {
            if (a.Identifier == value.Identifier) {
              description = a.Name;
              if (a.HasRawValueConversion | a.SensorType.HasValue)
                physical = a.ConvertValue(value);
              else
                physical = null;
            }
          }

          string raw = BitConverter.ToString(value.RawValue);
          r.AppendFormat(CultureInfo.InvariantCulture, 
            " {0}{1}{2}{3}{4}{5}{6}{7}",
            value.Identifier.ToString("X2").PadRight(3),
            description.PadRight(35),
            raw.Replace("-", "").PadRight(13),
            value.WorstValue.ToString(CultureInfo.InvariantCulture).PadRight(6),
            value.AttrValue.ToString(CultureInfo.InvariantCulture).PadRight(6),
            (threshold.HasValue ? threshold.Value.ToString(
              CultureInfo.InvariantCulture) : "-").PadRight(6),
            (physical.HasValue ? physical.Value.ToString(
              CultureInfo.InvariantCulture) : "-").PadRight(8),
            Environment.NewLine);
        }
        r.AppendLine();
      }

      return r.ToString();
    }

    protected static float RawToInt(byte[] raw, byte value) {
      return (raw[3] << 24) | (raw[2] << 16) | (raw[1] << 8) | raw[0];
    }

    public override void Close() {
      smart.CloseHandle(handle);
      base.Close();
    }

    public override void Traverse(IVisitor visitor) {
      foreach (ISensor sensor in Sensors)
        sensor.Accept(visitor);
    }
  }
}
