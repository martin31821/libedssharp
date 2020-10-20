/*
    This file is part of libEDSsharp.

    libEDSsharp is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    libEDSsharp is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with libEDSsharp.  If not, see <http://www.gnu.org/licenses/>.

    Copyright(c) 2016 - 2020 Robin Cornelius <robin.cornelius@gmail.com>
    based heavily on the files OD.h and OD.c from CANopenNode which is
    Copyright(c) 2010 - 2020 Janez Paternoster
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;

namespace libEDSsharp
{

    public class CanOpenNodeExporter_V3 : IExporter
    {
        private string odname;

        private List<string> ODStorageGroups;
        private Dictionary<string, List<string>> ODStorage_t;
        private Dictionary<string, List<string>> ODStorage;

        private List<string> ODObjs_t;
        private List<string> ODObjs;
        private List<string> ODExts_t;
        private List<string> ODList;
        private List<string> ODDefines;
        private List<string> ODDefinesLong;
        private SortedDictionary<string, UInt16> ODCnt;

        /// <summary>
        /// export the current data set in the CanOpen Node format V3
        /// </summary>
        /// <param name="folderpath"></param>
        /// <param name="filename"></param>
        /// <param name="gitVersion"></param>
        /// <param name="eds"></param>
        public void export(string folderpath, string filename, string gitVersion, EDSsharp eds, string odname)
        {
            this.odname = odname;

            Prepare(eds.ods);

            Export_h(folderpath, filename, gitVersion, eds.fi, eds.di);
            Export_c(folderpath, filename, gitVersion);
        }

        #region Prepare
        /// <summary>
        /// Generate ODStorage, ODObjs, ODExts, ODList, ODDefines and ODCnt entries
        /// </summary>
        /// <param name="ods"></param>
        private void Prepare(SortedDictionary<UInt16, ODentry> ods)
        {
            ODStorageGroups = new List<string>();
            ODStorage_t = new Dictionary<string, List<string>>();
            ODStorage = new Dictionary<string, List<string>>();

            ODExts_t = new List<string>();
            ODObjs_t = new List<string>();
            ODObjs = new List<string>();
            ODList = new List<string>();
            ODDefines = new List<string>();
            ODDefinesLong = new List<string>();
            ODCnt = new SortedDictionary<string, UInt16>();

            foreach (ODentry od in ods.Values)
            {
                if (od.Disabled == true)
                    continue;

                /* get data from eds */
                string indexH = $"{od.Index:X4}";
                string cName = Make_cname(od.parameter_name);
                string varName = $"{indexH}_{cName}";
                /* TODO get these from od, after available there */
                var extIO = false;
                var flagsPDO = false;
                string countLabel = "ALL";
                // UInt32 stringLength - implement this in Get_stringLength()
                // var accessSRDO - implement this in Get_attributes()

                /* verify extIO, this is absolutelly required for some objects. */
                if (!extIO)
                {
                    switch (od.Index)
                    {
                        case 0x1003:
                        case 0x1012:
                        case 0x1014:
                        case 0x1200:
                            extIO = true;
                            Warnings.AddWarning($"Error in 0x{indexH}: extIO must be enabled for this object!", Warnings.warning_class.WARNING_BUILD);
                            break;
                    }
                }

                /* storage group */
                string group = od.StorageLocation;
                if (ODStorageGroups.IndexOf(group) == -1)
                {
                    ODStorageGroups.Add(group);
                    ODStorage_t.Add(group, new List<string>());
                    ODStorage.Add(group, new List<string>());
                }

                string odObjectType = "";
                int subEntriesCount = 0;

                /* object type specific data */
                switch (od.objecttype)
                {
                    case ObjectType.VAR:
                        odObjectType = "VAR";
                        subEntriesCount = Prepare_var(od, indexH, varName, group);
                        break;

                    case ObjectType.ARRAY:
                        odObjectType = "ARR";
                        subEntriesCount = Prepare_arr(od, indexH, varName, group);
                        break;

                    case ObjectType.REC:
                        odObjectType = "REC";
                        subEntriesCount = Prepare_rec(od, indexH, varName, group);
                        break;
                }

                if (subEntriesCount < 1)
                    continue;

                /* extension */
                if (extIO || flagsPDO)
                {
                    string extIOAddr = "NULL";
                    string flagsPDOAddr = "NULL";
                    if (extIO)
                    {
                        ODExts_t.Add($"OD_extensionIO_t xio_{varName};");
                        extIOAddr = $"&{odname}Exts.xio_{varName}";
                    }
                    if (flagsPDO)
                    {
                        ODExts_t.Add($"OD_flagsPDO_t flp_{varName}[{subEntriesCount}];");
                        flagsPDOAddr = $"&{odname}_{group}.flp_{varName}[0]";
                    }
                    ODObjs_t.Add($"OD_obj_extended_t oE_{varName};");
                    ODObjs.Add($"    .oE_{varName} = {{");
                    ODObjs.Add($"        .extIO = {extIOAddr},");
                    ODObjs.Add($"        .flagsPDO = {flagsPDOAddr},");
                    ODObjs.Add($"        .odObjectOriginal = &{odname}Objs.o_{varName}");
                    ODObjs.Add($"    }},");
                }

                /* defines */
                ODDefines.Add($"#define {odname}_ENTRY_H{indexH} &{odname}.list[{ODList.Count}]");
                ODDefinesLong.Add($"#define {odname}_ENTRY_H{varName} &{odname}.list[{ODList.Count}]");

                /* object dictionary */
                string E = (extIO || flagsPDO) ? "E" : "";
                ODList.Add($"{{0x{indexH}, 0x{subEntriesCount:X2}, ODT_{E}{odObjectType}, &{odname}Objs.o{E}_{varName}}}");

                /* count labels */
                if (countLabel != null && countLabel != "")
                {
                    if (ODCnt.ContainsKey(countLabel))
                        ODCnt[countLabel]++;
                    else
                        ODCnt.Add(countLabel, 1);
                }
            }
        }

        /// <summary>
        /// Generate ODStorage and ODObjs entries for VAR
        /// </summary>
        /// <param name="od"></param>
        /// <param name="indexH"></param>
        /// <param name="varName"></param>
        /// <param name="group"></param>
        /// <returns></returns>
        private int Prepare_var(ODentry od, string indexH, string varName, string group)
        {
            DataProperties data = Get_dataProperties(od.datatype, od.defaultvalue, Get_stringLength(od), indexH);
            string attr = Get_attributes(od, data.cTypeMultibyte);

            /* data storage */
            string dataPtr = "NULL";
            if (data.cValue != null)
            {
                ODStorage_t[group].Add($"{data.cType} x{varName}{data.cTypeArray};");
                ODStorage[group].Add($".x{varName} = {data.cValue}");
                dataPtr = $"&{odname}_{group}.x{varName}{data.cTypeArray0}";
            }

            /* objects */
            ODObjs_t.Add($"OD_obj_var_t o_{varName};");
            ODObjs.Add($"    .o_{varName} = {{");
            ODObjs.Add($"        .data = {dataPtr},");
            ODObjs.Add($"        .attribute = {attr},");
            ODObjs.Add($"        .dataLength = {data.length}");
            ODObjs.Add($"    }},");

            return 1;
        }

        /// <summary>
        /// Generate ODStorage and ODObjs entries for ARRAY
        /// </summary>
        /// <param name="od"></param>
        /// <param name="indexH"></param>
        /// <param name="varName"></param>
        /// <param name="group"></param>
        /// <returns></returns>
        private int Prepare_arr(ODentry od, string indexH, string varName, string group)
        {
            int subEntriesCount = od.subobjects.Count;
            if (subEntriesCount < 2)
            {
                Warnings.AddWarning($"Error in 0x{indexH}: ARRAY must have minimum two sub entries, not {subEntriesCount}!", Warnings.warning_class.WARNING_BUILD);
                return 0;
            }

            /* prepare and verify each sub element */
            string cValue0 = "";
            DataProperties dataElem = new DataProperties();
            string attrElem0 = "";
            string attrElem = "";
            List<string> ODStorageValues = new List<string>();
            for (UInt16 i = 0; i < subEntriesCount; i++)
            {
                ODentry sub = od.subobjects[i];

                /* TODO verify how the things are stored in eds. sub.datatype should always be correct? */
                DataType dataType = (i == 0) ? sub.datatype : od.datatype;

                DataProperties data = Get_dataProperties(dataType, sub.defaultvalue, Get_stringLength(sub), indexH);
                string attr = Get_attributes(sub, data.cTypeMultibyte);

                if (sub.Subindex != i)
                    Warnings.AddWarning($"Error in 0x{indexH}: SubIndexes in ARRAY must be in sequence!", Warnings.warning_class.WARNING_BUILD);

                if (i == 0)
                {
                    if (data.cType != "uint8_t" || data.length != 1)
                        Warnings.AddWarning($"Error in 0x{indexH}: Data type in ARRAY in subIndex 0 must be UNSIGNED8, not {sub.datatype}!", Warnings.warning_class.WARNING_BUILD);

                    cValue0 = data.cValue;
                    attrElem0 = attr;
                }
                else
                {
                    if (i == 1)
                    {
                        dataElem = data;
                        attrElem = attr;
                    }
                    else
                    {
                        if (data.cType != dataElem.cType || data.length != dataElem.length)
                            Warnings.AddWarning($"Error in 0x{indexH}: Data type of elements in ARRAY must be equal!", Warnings.warning_class.WARNING_BUILD);
                        if ((data.cValue == null && dataElem.cValue != null) || (data.cValue != null && dataElem.cValue == null))
                            Warnings.AddWarning($"Error in 0x{indexH}: Default value must be defined on all ARRAY elements or must be undefined on all ARRAY elements!", Warnings.warning_class.WARNING_BUILD);
                        if (attr != attrElem)
                            Warnings.AddWarning($"Error in 0x{indexH}: Attributes of elements in ARRAY must be equal", Warnings.warning_class.WARNING_BUILD);
                    }
                    ODStorageValues.Add($"{data.cValue}");
                }
            }
            string dataPtr0 = "NULL";
            string dataPtr = "NULL";
            if (cValue0 != null)
            {
                ODStorage_t[group].Add($"uint8_t x{varName}_sub0;");
                ODStorage[group].Add($".x{varName}_sub0 = {cValue0}");
                dataPtr0 = $"&{odname}_{group}.x{varName}_sub0";
            }
            if (dataElem.cValue != null)
            {
                ODStorage_t[group].Add($"{dataElem.cType} x{varName}[{subEntriesCount - 1}]{dataElem.cTypeArray};");
                ODStorage[group].Add($".x{varName} = {{{string.Join(", ", ODStorageValues)}}}");
                dataPtr = $"&{odname}_{group}.x{varName}[0]{dataElem.cTypeArray0}";
            }

            /* objects */
            ODObjs_t.Add($"OD_obj_array_t o_{varName};");
            ODObjs.Add($"    .o_{varName} = {{");
            ODObjs.Add($"        .data0 = {dataPtr0},");
            ODObjs.Add($"        .data = {dataPtr},");
            ODObjs.Add($"        .attribute0 = {attrElem0},");
            ODObjs.Add($"        .attribute = {attrElem},");
            ODObjs.Add($"        .dataElementLength = {dataElem.length},");
            ODObjs.Add($"        .dataElementSizeof = sizeof({dataElem.cType})");
            ODObjs.Add($"    }},");

            return subEntriesCount;
        }

        /// <summary>
        /// Generate ODStorage and ODObjs entries for RECORD
        /// </summary>
        /// <param name="od"></param>
        /// <param name="indexH"></param>
        /// <param name="varName"></param>
        /// <param name="group"></param>
        /// <returns></returns>
        private int Prepare_rec(ODentry od, string indexH, string varName, string group)
        {
            int subEntriesCount = od.subobjects.Count;
            if (subEntriesCount < 2)
            {
                Warnings.AddWarning($"Error in 0x{indexH}: RECORD must have minimum two sub entries, not {subEntriesCount}!", Warnings.warning_class.WARNING_BUILD);
                return 0;
            }

            List<string> subODStorage_t = new List<string>();
            List<string> subODStorage = new List<string>();

            ODObjs_t.Add($"OD_obj_record_t o_{varName}[{subEntriesCount}];");
            ODObjs.Add($"    .o_{varName} = {{");

            for (UInt16 i = 0; i < subEntriesCount; i++)
            {
                ODentry sub = od.subobjects[i];

                DataProperties data = Get_dataProperties(sub.datatype, sub.defaultvalue, Get_stringLength(sub), indexH);
                string attr = Get_attributes(sub, data.cTypeMultibyte);

                if (i == 0 && (sub.Subindex != 0 || data.cType != "uint8_t" || data.length != 1))
                    Warnings.AddWarning($"Error in 0x{indexH}: Data type in RECORD, first sub-entry, subIndex 0 must be UNSIGNED8, not {sub.datatype}!", Warnings.warning_class.WARNING_BUILD);

                string subcName = Make_cname(sub.parameter_name);
                string dataPtr = "NULL";
                if (data.cValue != null)
                {
                    subODStorage_t.Add($"{data.cType} {subcName}{data.cTypeArray};");
                    subODStorage.Add($".{subcName} = {data.cValue}");
                    dataPtr = $"&{odname}_{group}.x{varName}.{subcName}{data.cTypeArray0}";
                }
                ODObjs.Add($"        {{");
                ODObjs.Add($"            .data = {dataPtr},");
                ODObjs.Add($"            .subIndex = {sub.Subindex},");
                ODObjs.Add($"            .attribute = {attr},");
                ODObjs.Add($"            .dataLength = {data.length}");
                ODObjs.Add($"        }},");

            }
            /* remove last ',' and add closing bracket */
            string s = ODObjs[ODObjs.Count - 1];
            ODObjs[ODObjs.Count - 1] = s.Remove(s.Length - 1);
            ODObjs.Add($"    }},");

            if (subODStorage_t.Count > 0)
            {
                ODStorage_t[group].Add($"struct {{\n        {string.Join("\n        ", subODStorage_t)}\n    }} x{varName};");
                ODStorage[group].Add($".x{varName} = {{\n        {string.Join(",\n        ", subODStorage)}\n    }}");
            }

            return subEntriesCount;
        }
        #endregion

        #region Exporters

        /// <summary>
        /// Export the header file
        /// </summary>
        /// <param name="folderpath"></param>
        /// <param name="filename"></param>
        /// <param name="gitVersion"></param>
        public void Export_h(string folderpath, string filename, string gitVersion, FileInfo fi, DeviceInfo di)
        {

            if (filename == "")
                filename = "OD";

            StreamWriter file = new StreamWriter(folderpath + Path.DirectorySeparatorChar + filename + ".h");

            file.WriteLine(string.Format(
@"/*******************************************************************************
    CANopen Object Dictionary.

        This file was automatically generated with
        libedssharp Object Dictionary Editor v{0}

    DON'T EDIT THIS FILE MANUALLY !!!!
********************************************************************************

    File info:
        FileName:       {1}
        FileVersion:    {2}
        CreationTime:   {3}
        CreationDate:   {4}
        CreatedBy:      {5}

    Device Info:
        VendorName:     {6}
        VendorNumber:   {7}
        ProductName:    {8}
        ProductNumber:  {9}
*******************************************************************************/",
            gitVersion,
            fi.FileName, fi.FileVersion, fi.CreationTime, fi.CreationDate, fi.CreatedBy,
            di.VendorName, di.VendorNumber, di.ProductName, di.ProductNumber));

            file.WriteLine(string.Format(@"
#ifndef {0}_H
#define {0}_H
/*******************************************************************************
    Counters of OD objects
*******************************************************************************/",
            odname));

            foreach (KeyValuePair<string, UInt16> kvp in ODCnt)
            {
                Console.WriteLine($"#define {odname}_CNT_{kvp.Key} {kvp.Value}");
            }

            file.WriteLine(@"

/*******************************************************************************
    OD data declaration of all groups
*******************************************************************************/");
            foreach (string group in ODStorageGroups)
            {
                if (ODStorage_t.Count > 0)
                {
                    file.WriteLine($"typedef struct {{");
                    file.WriteLine($"    {string.Join("\n    ", ODStorage_t[group])}");
                    file.WriteLine($"}} {odname}_{group}_t;\n");
                }
            }

            foreach (string group in ODStorageGroups)
            {
                if (ODStorage_t.Count > 0)
                {
                    file.WriteLine($"extern {odname}_{group}_t {odname}_{group};");
                }
            }
            file.WriteLine($"extern const OD_t {odname};");

            file.WriteLine(string.Format(@"

/*******************************************************************************
    Object dictionary entries - shortcuts
*******************************************************************************/
{0}", string.Join("\n", ODDefines)));

            file.WriteLine(string.Format(@"

/*******************************************************************************
    Object dictionary entries - shortcuts with names
*******************************************************************************/
{1}

#endif /* {0}_H */",
            odname, string.Join("\n", ODDefinesLong)));

            file.Close();
        }

        /// <summary>
        /// Export the c file
        /// </summary>
        /// <param name="folderpath"></param>
        /// <param name="filename"></param>
        /// <param name="gitVersion"></param>
        public void Export_c(string folderpath, string filename, string gitVersion)
            {

            if (filename == "")
                filename = "OD";

            StreamWriter file = new StreamWriter(folderpath + Path.DirectorySeparatorChar + filename + ".c");

            file.WriteLine(string.Format(
@"/*******************************************************************************
    CANopen Object Dictionary.

        This file was automatically generated with
        libedssharp Object Dictionary Editor v{0}

    DON'T EDIT THIS FILE MANUALLY, UNLESS YOU KNOW WHAT YOU ARE DOING !!!!
*******************************************************************************/

#define OD_DEFINITION
#include ""301/CO_ODinterface.h""
#include ""{1}.h""", gitVersion, filename));

            file.WriteLine(@"
/*******************************************************************************
    OD data initialization of all groups
*******************************************************************************/");
            foreach (string group in ODStorageGroups)
            {
                if (ODStorage.Count > 0)
                {
                    file.WriteLine($"{odname}_{group}_t {odname}_{group} = {{");
                    file.WriteLine($"    {string.Join(",\n    ", ODStorage[group])}");
                    file.WriteLine($"}};\n");
                }
            }

            if (ODExts_t.Count > 0)
            {
                file.WriteLine(string.Format(@"
/*******************************************************************************
    IO extensions and flagsPDO (configurable by application)
*******************************************************************************/
typedef struct {{
    {1}
}} {0}Exts_t;

static {0}Exts_t {0}Exts = {{0}};", odname, string.Join("\n    ", ODExts_t)));
            }

            /* remove ',' from the last element */
            string s = ODObjs[ODObjs.Count - 1];
            ODObjs[ODObjs.Count - 1] = s.Remove(s.Length - 1);

            file.WriteLine(string.Format(@"

/*******************************************************************************
    All OD objects (const)
*******************************************************************************/
typedef struct {{
    {1}
}} {0}Objs_t;

static const {0}Objs_t {0}Objs = {{
{2}
}};", odname, string.Join("\n    ", ODObjs_t), string.Join("\n", ODObjs)));

            file.WriteLine(string.Format(@"

/*******************************************************************************
    Object dictionary
*******************************************************************************/
static const OD_entry_t {0}List[] = {{
    {1},
    {{0x0000, 0x00, 0, NULL}}
}};

const OD_t {0} = {{
    (sizeof({0}List) / sizeof({0}List[0])) - 1,
    &{0}List[0]
}};", odname, string.Join(",\n    ", ODList)));

            file.Close();
        }
        #endregion

        #region helper_functions

        /// <summary>
        /// Take a paramater name from the object dictionary and make it acceptable
        /// for use in c variables/structs etc
        /// </summary>
        /// <param name="name">string, name to convert</param>
        /// <returns>string</returns>
        private string Make_cname(string name)
        {
            if (name == null || name == "")
                return "";

            Regex splitter = new Regex(@"[\W]+");

            var bits = splitter.Split(name).Where(s => s != String.Empty);

            string output = "";

            char lastchar = ' ';
            foreach (string s in bits)
            {
                if (Char.IsUpper(lastchar) && Char.IsUpper(s.First()))
                    output += "_";

                if (s.Length > 1)
                {
                    output += char.ToUpper(s[0]) + s.Substring(1);
                }
                else
                {
                    output += s;
                }

                if (output.Length > 0)
                    lastchar = output.Last();

            }

            if (output.Length > 1)
            {
                if (Char.IsLower(output[1]))
                    output = Char.ToLower(output[0]) + output.Substring(1);
            }
            else
                output = output.ToLower(); //single character

            return output;
        }

        /// <summary>
        /// Return from Get_dataProperties
        /// </summary>
        private struct DataProperties
        {
            public string cType;
            public string cTypeArray;
            public string cTypeArray0;
            public bool cTypeMultibyte;
            public UInt32 length;
            public string cValue;
        }

        /// <summary>
        /// Get the correct c data type, length and default value, based on CANopen data type
        /// </summary>
        /// <param name="dataType"></param>
        /// <param name="defaultvalue"></param>
        /// <param name="stringLength"></param>
        /// <param name="indexH"></param>
        /// <returns>Structure filled with data</returns>
        private DataProperties Get_dataProperties(DataType dataType, string defaultvalue, UInt32 stringLength, string indexH)
        {
            DataProperties data = new DataProperties
            {
                cType = "not specified",
                cTypeArray = "",
                cTypeArray0 = "",
                cTypeMultibyte = false,
                length = 0,
                cValue = null
            };

            int nobase = 10;
            bool valueDefined = true;
            if (defaultvalue == null || defaultvalue == "")
                valueDefined = false;
            else if (dataType != DataType.VISIBLE_STRING && dataType != DataType.UNICODE_STRING && dataType != DataType.OCTET_STRING)
            {
                defaultvalue = defaultvalue.Trim();
                if (defaultvalue == "")
                    valueDefined = false;
                else
                {
                    if (defaultvalue.Contains("$NODEID"))
                    {
                        defaultvalue = defaultvalue.Replace("$NODEID", "");
                        defaultvalue = defaultvalue.Replace("+", "");
                        defaultvalue = defaultvalue.Trim();
                        if (defaultvalue == "")
                            defaultvalue = "0";
                    }

                    String pat = @"^0[xX][0-9a-fA-FUL]+";
                    Regex r = new Regex(pat, RegexOptions.IgnoreCase);
                    Match m = r.Match(defaultvalue);
                    if (m.Success)
                    {
                        nobase = 16;
                        defaultvalue = defaultvalue.Replace("U", "");
                        defaultvalue = defaultvalue.Replace("L", "");
                    }

                    pat = @"^0[0-7]+";
                    r = new Regex(pat, RegexOptions.IgnoreCase);
                    m = r.Match(defaultvalue);
                    if (m.Success)
                    {
                        nobase = 8;
                    }
                }
            }

            try
            {
                bool signedNumber = false;
                bool unsignedNumber = false;

                switch (dataType)
                {
                    case DataType.BOOLEAN:
                        data.length = 1;
                        if (valueDefined)
                        {
                            data.cType = "bool_t";
                            data.cValue = (defaultvalue.ToLower() == "false" || defaultvalue == "0") ? "false" : "true";
                        }
                        break;
                    case DataType.INTEGER8:
                        data.length = 1;
                        if (valueDefined)
                        {
                            data.cType = "int8_t";
                            data.cValue = $"{Convert.ToSByte(defaultvalue, nobase)}";
                        }
                        break;
                    case DataType.INTEGER16:
                        data.length = 2;
                        data.cTypeMultibyte = true;
                        if (valueDefined)
                        {
                            data.cType = "int16_t";
                            data.cValue = $"{Convert.ToInt16(defaultvalue, nobase)}";
                        }
                        break;
                    case DataType.INTEGER32:
                        data.length = 4;
                        data.cTypeMultibyte = true;
                        if (valueDefined)
                        {
                            data.cType = "int32_t";
                            data.cValue = $"{Convert.ToInt32(defaultvalue, nobase)}";
                        }
                        break;
                    case DataType.INTEGER64:
                        data.length = 8;
                        data.cTypeMultibyte = true;
                        if (valueDefined)
                        {
                            data.cType = "int64_t";
                            data.cValue = $"{Convert.ToInt64(defaultvalue, nobase)}";
                        }
                        break;

                    case DataType.UNSIGNED8:
                        data.length = 1;
                        if (valueDefined)
                        {
                            data.cType = "uint8_t";
                            data.cValue = String.Format("0x{0:X2}", Convert.ToByte(defaultvalue, nobase));
                        }
                        break;
                    case DataType.UNSIGNED16:
                        data.length = 2;
                        data.cTypeMultibyte = true;
                        if (valueDefined)
                        {
                            data.cType = "uint16_t";
                            data.cValue = String.Format("0x{0:X4}", Convert.ToUInt16(defaultvalue, nobase));
                        }
                        break;
                    case DataType.UNSIGNED32:
                        data.length = 4;
                        data.cTypeMultibyte = true;
                        if (valueDefined)
                        {
                            data.cType = "uint32_t";
                            data.cValue = String.Format("0x{0:X8}", Convert.ToUInt32(defaultvalue, nobase));
                        }
                        break;
                    case DataType.UNSIGNED64:
                        data.length = 8;
                        data.cTypeMultibyte = true;
                        if (valueDefined)
                        {
                            data.cType = "uint64_t";
                            data.cValue = String.Format("0x{0:X16}", Convert.ToUInt64(defaultvalue, nobase));
                        }
                        break;

                    case DataType.REAL32:
                        data.length = 4;
                        data.cTypeMultibyte = true;
                        if (valueDefined)
                        {
                            data.cType = "float32_t";
                            data.cValue = defaultvalue;
                        }
                        break;
                    case DataType.REAL64:
                        data.length = 8;
                        data.cTypeMultibyte = true;
                        if (valueDefined)
                        {
                            data.cType = "float64_t";
                            data.cValue = defaultvalue;
                        }
                        break;

                    case DataType.DOMAIN:
                        /* keep default values (0 and null) */
                        break;

                    case DataType.VISIBLE_STRING:
                        if (valueDefined || stringLength > 0)
                        {
                            List<string> chars = new List<string>();
                            UInt32 len = 0;

                            if (valueDefined)
                            {
                                Encoding ascii = Encoding.ASCII;
                                Byte[] encodedBytes = ascii.GetBytes(defaultvalue);
                                foreach (Byte b in encodedBytes)
                                {
                                    chars.Add($"'{StringUnescape.Escape((char)b)}'");
                                    len++;
                                }
                            }
                            for (; len < stringLength; len++)
                            {
                                chars.Add("'\\0'");
                            }

                            data.length = len;
                            data.cType = "char";
                            data.cTypeArray = $"[{len}]";
                            data.cTypeArray0 = "[0]";
                            data.cValue = $"{{{string.Join(", ", chars)}}}";
                        }
                        break;

                    case DataType.OCTET_STRING:
                        defaultvalue = defaultvalue.Trim();
                        if (defaultvalue == "")
                            valueDefined = false;
                        if (valueDefined || stringLength > 0)
                        {
                            List<string> bytes = new List<string>();
                            UInt32 len = 0;

                            if (valueDefined)
                            {
                                string[] strBytes = defaultvalue.Split(' ');
                                foreach (string s in strBytes)
                                {
                                    bytes.Add(String.Format("0x{0:X2}", Convert.ToByte(s, nobase)));
                                    len++;
                                }
                            }
                            for (; len < stringLength; len++)
                            {
                                bytes.Add("0x00");
                            }

                            data.length = len;
                            data.cType = "uint8_t";
                            data.cTypeArray = $"[{len}]";
                            data.cTypeArray0 = "[0]";
                            data.cValue = $"{{{string.Join(", ", bytes)}}}";
                        }
                        break;
                    case DataType.UNICODE_STRING:
                        if (valueDefined || stringLength > 0)
                        {
                            List<string> words = new List<string>();
                            UInt32 len = 0;

                            if (valueDefined)
                            {
                                Encoding unicode = Encoding.Unicode;
                                Byte[] encodedBytes = unicode.GetBytes(defaultvalue);
                                for (UInt32 i = 0; i < encodedBytes.Length; i += 2)
                                {
                                    UInt16 val = (ushort)(encodedBytes[i] | (UInt16)encodedBytes[i+1] << 8);
                                    words.Add(String.Format("0x{0:X4}", val));
                                    len++;
                                }
                            }
                            for (; len < stringLength; len++)
                            {
                                words.Add("0x0000");
                            }

                            data.length = len * 2;
                            data.cType = "uint16_t";
                            data.cTypeArray = $"[{len}]";
                            data.cTypeArray0 = "[0]";
                            data.cValue = $"{{{string.Join(", ", words)}}}";
                        }
                        break;

                    case DataType.INTEGER24:
                        data.length = 3;
                        signedNumber = true;
                        break;
                    case DataType.INTEGER40:
                        data.length = 5;
                        signedNumber = true;
                        break;
                    case DataType.INTEGER48:
                        data.length = 6;
                        signedNumber = true;
                        break;
                    case DataType.INTEGER56:
                        data.length = 7;
                        signedNumber = true;
                        break;

                    case DataType.UNSIGNED24:
                        data.length = 3;
                        unsignedNumber = true;
                        break;
                    case DataType.UNSIGNED40:
                        data.length = 5;
                        unsignedNumber = true;
                        break;
                    case DataType.UNSIGNED48:
                    case DataType.TIME_OF_DAY:
                    case DataType.TIME_DIFFERENCE:
                        data.length = 6;
                        unsignedNumber = true;
                        break;
                    case DataType.UNSIGNED56:
                        data.length = 7;
                        unsignedNumber = true;
                        break;

                    default:
                        Warnings.AddWarning($"Error in 0x{indexH}: Unknown dataType: {dataType}", Warnings.warning_class.WARNING_BUILD);
                        break;
                }

                if (valueDefined && (signedNumber || unsignedNumber))
                {
                    /* write default value as a sequence of bytes, like "{0x56, 0x34, 0x12}" */
                    ulong value = signedNumber ? (ulong)Convert.ToInt64(defaultvalue, nobase) : Convert.ToUInt64(defaultvalue, nobase);
                    List<string> bytes = new List<string>();
                    for (UInt32 i = 0; i < data.length; i++)
                    {
                        bytes.Add(String.Format("0x{0:X2}", value & 0xFF));
                        value >>= 8;
                    }
                    if (value > 0)
                        Warnings.AddWarning($"Error in 0x{indexH}: Overflow error in default value {defaultvalue} of type {dataType}", Warnings.warning_class.WARNING_BUILD);
                    else
                    {
                        data.cType = "uint8_t";
                        data.cTypeArray = $"[{data.length}]";
                        data.cTypeArray0 = "[0]";
                        data.cValue = $"{{{string.Join(", ", bytes)}}}";
                    }
                }
            }
            catch (Exception)
            {
                Warnings.AddWarning($"Error in 0x{indexH}: Error converting default value {defaultvalue} to type {dataType}", Warnings.warning_class.WARNING_BUILD);
            }

            return data;
        }

        /// <summary>
        /// Get attributes from OD entry or sub-entry
        /// </summary>
        /// <param name="od"></param>
        /// <param name="multibyte"></param>
        /// <returns></returns>
        private string Get_attributes(ODentry od, bool multibyte)
        {
            List<string> attributes = new List<string>();

            switch (od.accesstype)
            {
                case EDSsharp.AccessType.rw:
                case EDSsharp.AccessType.rwr:
                case EDSsharp.AccessType.rww:
                    attributes.Add("ODA_SDO_RW");
                    break;
                case EDSsharp.AccessType.ro:
                case EDSsharp.AccessType.@const:
                    attributes.Add("ODA_SDO_R");
                    break;
                case EDSsharp.AccessType.wo:
                    attributes.Add("ODA_SDO_W");
                    break;
            }

            switch (od.PDOtype)
            {
                case PDOMappingType.optional:
                    attributes.Add("ODA_TRPDO");
                    break;
                case PDOMappingType.TPDO:
                    attributes.Add("ODA_TPDO");
                    break;
                case PDOMappingType.RPDO:
                    attributes.Add("ODA_RPDO");
                    break;
            }

            //we currently have no support for SRDO in the object dictionary editor

            if (multibyte)
                attributes.Add("ODA_MB");

            return string.Join(" | ", attributes);
        }

        /// <summary>
        /// Get stringLength custom property from OD entry or sub-entry
        /// </summary>
        /// <param name="od"></param>
        /// <returns></returns>
        private UInt32 Get_stringLength(ODentry od)
        {
            return 8;
        }

        #endregion
    }
}