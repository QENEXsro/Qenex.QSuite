// Verification of MatrixVariable (value block / curve / map over a raw byte array)
// and its XML round-trip through XmlVariableMapper.
//
// Scenarios:
//   T1  map layout 3x10, all byte: section counts, offsets, total size (the 43 B example)
//   T2  mixed element types + big endian: offsets, raw read/write round-trip
//   T3  engineering values: per-section linear conversion, write via inverse conversion,
//       overflow and NaN rejected
//   T4  presentation text honours per-section PrintFormat
//   T5  XML round-trip of map / curve / value block shapes preserves the whole layout
//   T6  layout validation rejects Y axis without X axis, String elements, empty sections
//   T7  SetValue accepts only a byte[] of exactly Size bytes
//   T8  Modbus byte<->register codec round-trips, including an odd byte count
//   T9  Modbus variable specification for a matrix: register count, limits, bit-table rejection
//   T10 pending write queue carries the edited bytes in FIFO order

using System.Globalization;
using System.Xml.Serialization;
using Qenex.QSuite.ModuleXmlHandler;
using Qenex.QSuite.ModuleXmlHandler.XmlStructure;
using Qenex.QSuite.Protocols.Modbus;
using Qenex.QSuite.Variables.QVariables;
using Qenex.QSuite.Variables.ValueConversion;
using Qenex.QSuite.Variables.ValuePresentation;
using ValueDataType = Qenex.QSuite.Variables.QVariables.Values.ValuesGlobal.ValueDataType;

var results = new List<(string Name, bool Pass, string Detail)>();

RunTest("T1 map layout 3x10, all byte (43 B example)", Test1_MapLayoutBytes);
RunTest("T2 mixed element types + big endian raw round-trip", Test2_MixedTypesBigEndian);
RunTest("T3 engineering values per section, inverse write, overflow", Test3_EngValues);
RunTest("T4 presentation text per section PrintFormat", Test4_PresentationText);
RunTest("T5 XML round-trip: map, curve, value block", Test5_XmlRoundTrip);
RunTest("T6 layout validation", Test6_Validation);
RunTest("T7 SetValue length check", Test7_SetValue);
RunTest("T8 Modbus byte/register codec round-trip", Test8_ModbusByteCodec);
RunTest("T9 Modbus matrix variable specification", Test9_ModbusMatrixSpec);
RunTest("T10 pending write queue FIFO", Test10_PendingWriteQueue);

Console.WriteLine();
Console.WriteLine("==== SUMMARY ====");
foreach (var (name, pass, detail) in results)
{
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {name}");
    if (!string.IsNullOrWhiteSpace(detail)) Console.WriteLine($"      {detail}");
}

return results.All(r => r.Pass) ? 0 : 1;

void RunTest(string name, Func<(bool, string)> test)
{
    Console.WriteLine();
    Console.WriteLine($"---- {name} ----");
    try
    {
        var (pass, detail) = test();
        results.Add((name, pass, detail));
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} {detail}");
    }
    catch (Exception e)
    {
        results.Add((name, false, $"EXCEPTION: {e}"));
        Console.WriteLine($"FAIL EXCEPTION: {e}");
    }
}

// ---------------------------------------------------------------- helpers

static IPresentation LinearPresentation(string name, double multiplier, double offset, string printFormat = "", string unit = "")
    => new Presentation
    {
        Name = name,
        PrintFormat = printFormat,
        Unit = unit,
        Conversion = new LinearValConversion { Multiplier = multiplier, Offset = offset }
    };

static MatrixVariable MapVariable43B()
{
    // Radek's example: 43 B = 3 B X axis + 10 B Y axis + 30 B data 3x10, 1 B per value
    return new MatrixVariable
    {
        Id = 1,
        Namespace = "/",
        Name = "IgnitionMap",
        Label = "Ignition map",
        Description = "3x10 calibration map",
        DefaultDataType = ValueDataType.Byte,
        Endianness = MatrixEndianness.Little,
        XAxis = new MatrixSection { Count = 3 },
        YAxis = new MatrixSection { Count = 10 },
        Data = new MatrixSection()
    };
}

// ---------------------------------------------------------------- tests

(bool, string) Test1_MapLayoutBytes()
{
    var variable = MapVariable43B();

    var checks = new (string What, bool Ok)[]
    {
        ("valid layout", variable.ValidateLayout() == null),
        ("XCount == 3", variable.XCount == 3),
        ("YCount == 10", variable.YCount == 10),
        ("DataCount == 30", variable.DataCount == 30),
        ("Size == 43", variable.Size == 43),
        ("X offset == 0", variable.GetSectionOffset(MatrixSectionKind.XAxis) == 0),
        ("Y offset == 3", variable.GetSectionOffset(MatrixSectionKind.YAxis) == 3),
        ("data offset == 13", variable.GetSectionOffset(MatrixSectionKind.Data) == 13),
        ("element sizes == 1", variable.GetElementSize(MatrixSectionKind.Data) == 1)
    };

    var failed = checks.Where(c => !c.Ok).Select(c => c.What).ToList();
    return (failed.Count == 0, failed.Count == 0 ? "layout ok" : "failed: " + string.Join(", ", failed));
}

(bool, string) Test2_MixedTypesBigEndian()
{
    // axes 1 B, data ushort 2 B, big endian: 3 + 10 + 60 = 73 B
    var variable = MapVariable43B();
    variable.Data = new MatrixSection { DataType = ValueDataType.UShort };
    variable.Endianness = MatrixEndianness.Big;

    if (variable.Size != 73)
    {
        return (false, $"Size expected 73, got {variable.Size}");
    }

    if (variable.GetSectionOffset(MatrixSectionKind.Data) != 13)
    {
        return (false, "data offset expected 13");
    }

    // write raw bytes directly: data element index 1 = 0x0102 big endian
    var buffer = new byte[73];
    buffer[13 + 2] = 0x01;
    buffer[13 + 3] = 0x02;
    buffer[0] = 5; // X axis breakpoint 0
    variable.SetValue(buffer);

    if (Math.Abs(variable.GetRawValue(MatrixSectionKind.Data, 1) - 0x0102) > 0)
    {
        return (false, $"big endian read expected 258, got {variable.GetRawValue(MatrixSectionKind.Data, 1)}");
    }

    if (Math.Abs(variable.GetRawValue(MatrixSectionKind.XAxis, 0) - 5) > 0)
    {
        return (false, "X axis read failed");
    }

    // write through the element API and read the bytes back
    if (!variable.TrySetEngValue(MatrixSectionKind.Data, 2, 0x0304))
    {
        return (false, "TrySetEngValue refused a plain raw write");
    }

    var raw = (byte[])variable.GetValue();
    return (raw[13 + 4] == 0x03 && raw[13 + 5] == 0x04, "mixed types + big endian ok");
}

(bool, string) Test3_EngValues()
{
    var variable = MapVariable43B();
    variable.Data.Presentation = LinearPresentation("IgnitionAngle", 0.5, 10);  // eng = raw * 0.5 + 10
    variable.XAxis!.Presentation = LinearPresentation("Rpm", 100, 0);           // eng = raw * 100

    var buffer = new byte[43];
    buffer[0] = 20;       // X[0] raw 20 -> eng 2000
    buffer[13] = 40;      // data[0] raw 40 -> eng 30
    variable.SetValue(buffer);

    if (Math.Abs(variable.GetEngValue(MatrixSectionKind.XAxis, 0) - 2000) > 1e-9)
    {
        return (false, $"X eng expected 2000, got {variable.GetEngValue(MatrixSectionKind.XAxis, 0)}");
    }

    if (Math.Abs(variable.GetEngValue(MatrixSectionKind.Data, 0) - 30) > 1e-9)
    {
        return (false, $"data eng expected 30, got {variable.GetEngValue(MatrixSectionKind.Data, 0)}");
    }

    // inverse write: eng 35 -> raw (35 - 10) / 0.5 = 50
    if (!variable.TrySetEngValue(MatrixSectionKind.Data, 5, 35))
    {
        return (false, "TrySetEngValue(35) failed");
    }
    if (Math.Abs(variable.GetRawValue(MatrixSectionKind.Data, 5) - 50) > 0)
    {
        return (false, $"inverse write expected raw 50, got {variable.GetRawValue(MatrixSectionKind.Data, 5)}");
    }

    // overflow: eng 300 -> raw 580 > byte max -> refused, bytes untouched
    if (variable.TrySetEngValue(MatrixSectionKind.Data, 5, 300))
    {
        return (false, "overflow write was not refused");
    }
    if (Math.Abs(variable.GetRawValue(MatrixSectionKind.Data, 5) - 50) > 0)
    {
        return (false, "refused write modified the buffer");
    }

    // NaN refused
    if (variable.TrySetEngValue(MatrixSectionKind.Data, 5, double.NaN))
    {
        return (false, "NaN write was not refused");
    }

    return (true, "eng conversions ok");
}

(bool, string) Test4_PresentationText()
{
    var variable = MapVariable43B();
    variable.Data.Presentation = LinearPresentation("IgnitionAngle", 0.5, 0, "{0:F1}", "deg");

    var buffer = new byte[43];
    buffer[13] = 5; // raw 5 -> eng 2.5
    variable.SetValue(buffer);

    var text = variable.GetPresentationText(MatrixSectionKind.Data, 0);
    if (text != "2.5")
    {
        return (false, $"expected \"2.5\", got \"{text}\"");
    }

    var plain = variable.GetPresentationText(MatrixSectionKind.YAxis, 0); // no presentation -> raw
    return (plain == "0", $"formatted \"{text}\", plain \"{plain}\"");
}

(bool, string) Test5_XmlRoundTrip()
{
    var presentations = new List<IPresentation>
    {
        LinearPresentation("Rpm", 100, 0),
        LinearPresentation("Temperature", 1, -40),
        LinearPresentation("IgnitionAngle", 0.5, 10, "{0:F1}", "deg")
    };

    // map: axes byte (default), data ushort override, big endian
    var map = MapVariable43B();
    map.Endianness = MatrixEndianness.Big;
    map.Data = new MatrixSection { DataType = ValueDataType.UShort, Presentation = presentations[2] };
    map.XAxis!.Presentation = presentations[0];
    map.XAxis.Label = "Engine speed";
    map.YAxis!.Presentation = presentations[1];

    // curve: X axis only, no per-section overrides, no presentations
    var curve = new MatrixVariable
    {
        Id = 2, Namespace = "/", Name = "TorqueCurve",
        DefaultDataType = ValueDataType.Float,
        XAxis = new MatrixSection { Count = 8 },
        Data = new MatrixSection()
    };

    // value block: no axes, explicit data count
    var block = new MatrixVariable
    {
        Id = 3, Namespace = "/", Name = "RawBlock",
        DefaultDataType = ValueDataType.Byte,
        Data = new MatrixSection { Count = 5 }
    };

    var xmlVariables = XmlVariableMapper.ToXmlVariables([map, curve, block]);
    if (xmlVariables.Count != 3)
    {
        return (false, $"ToXmlVariables produced {xmlVariables.Count} variables, expected 3");
    }

    // serialize + deserialize the standalone variables file
    var serializer = new XmlSerializer(typeof(XmlVariablesFile));
    string xmlText;
    using (var writer = new StringWriter(CultureInfo.InvariantCulture))
    {
        serializer.Serialize(writer, new XmlVariablesFile { Variables = xmlVariables });
        xmlText = writer.ToString();
    }

    if (!xmlText.Contains("<matrixVariable") || !xmlText.Contains("endianness=\"be\""))
    {
        return (false, "serialized XML misses matrixVariable/endianness");
    }
    // axes of the map use the default type -> no dataType attribute on the section
    if (xmlText.Contains("<xAxis count=\"3\" dataType"))
    {
        return (false, "default-typed section serialized an explicit dataType");
    }

    XmlVariablesFile reloaded;
    using (var reader = new StringReader(xmlText))
    {
        reloaded = (XmlVariablesFile)serializer.Deserialize(reader)!;
    }

    var variables = XmlVariableMapper.FromXmlVariables(reloaded.Variables, presentations);
    if (variables.Count != 3)
    {
        return (false, $"FromXmlVariables produced {variables.Count} variables, expected 3");
    }

    var map2 = variables.OfType<MatrixVariable>().First(v => v.Name == "IgnitionMap");
    var curve2 = variables.OfType<MatrixVariable>().First(v => v.Name == "TorqueCurve");
    var block2 = variables.OfType<MatrixVariable>().First(v => v.Name == "RawBlock");

    var checks = new (string What, bool Ok)[]
    {
        ("map size 73", map2.Size == 73),
        ("map endianness big", map2.Endianness == MatrixEndianness.Big),
        ("map default type byte", map2.DefaultDataType == ValueDataType.Byte),
        ("map X count 3", map2.XCount == 3),
        ("map Y count 10", map2.YCount == 10),
        ("map data type ushort", map2.GetSectionDataType(MatrixSectionKind.Data) == ValueDataType.UShort),
        ("map X axis type falls back to byte", map2.GetSectionDataType(MatrixSectionKind.XAxis) == ValueDataType.Byte),
        ("map X presentation resolved", map2.XAxis?.Presentation?.Name == "Rpm"),
        ("map X axis label kept", map2.XAxis?.Label == "Engine speed"),
        ("map Y axis label empty", map2.YAxis?.Label == string.Empty),
        ("map Y presentation resolved", map2.YAxis?.Presentation?.Name == "Temperature"),
        ("map data presentation resolved", map2.Data.Presentation?.Name == "IgnitionAngle"),
        ("map description kept", map2.Description == "3x10 calibration map"),
        ("curve has no Y axis", curve2.YAxis == null),
        ("curve data count 8", curve2.DataCount == 8),
        ("curve size 8*4+8*4", curve2.Size == 64),
        ("curve default type float", curve2.DefaultDataType == ValueDataType.Float),
        ("curve sections have no presentation", curve2.Data.Presentation == null && curve2.XAxis?.Presentation == null),
        ("block has no axes", block2.XAxis == null && block2.YAxis == null),
        ("block data count 5", block2.DataCount == 5),
        ("block size 5", block2.Size == 5)
    };

    var failed = checks.Where(c => !c.Ok).Select(c => c.What).ToList();
    return (failed.Count == 0, failed.Count == 0 ? "XML round-trip ok" : "failed: " + string.Join(", ", failed));
}

(bool, string) Test6_Validation()
{
    var yWithoutX = new MatrixVariable
    {
        Name = "Bad1",
        YAxis = new MatrixSection { Count = 4 },
        Data = new MatrixSection()
    };
    if (yWithoutX.ValidateLayout() == null)
    {
        return (false, "Y axis without X axis was not rejected");
    }

    var stringType = new MatrixVariable
    {
        Name = "Bad2",
        DefaultDataType = ValueDataType.String,
        Data = new MatrixSection { Count = 4 }
    };
    if (stringType.ValidateLayout() == null)
    {
        return (false, "String element type was not rejected");
    }

    var emptyData = new MatrixVariable
    {
        Name = "Bad3",
        Data = new MatrixSection { Count = 0 }
    };
    if (emptyData.ValidateLayout() == null)
    {
        return (false, "empty data section was not rejected");
    }

    return (MapVariable43B().ValidateLayout() == null, "validation ok");
}

(bool, string) Test8_ModbusByteCodec()
{
    // even byte count round-trip
    byte[] even = [0x01, 0x02, 0x03, 0x04];
    var evenRegisters = ModbusRegisterCodec.EncodeBytes(even);
    if (evenRegisters.Length != 2 || evenRegisters[0] != 0x0102 || evenRegisters[1] != 0x0304)
    {
        return (false, "even encode wrong (expected high byte first per register)");
    }

    if (!ModbusRegisterCodec.DecodeBytes(evenRegisters, 4).SequenceEqual(even))
    {
        return (false, "even decode round-trip failed");
    }

    // odd byte count: final register's low byte padded with zero, decode truncates it back
    byte[] odd = [0xAA, 0xBB, 0xCC];
    var oddRegisters = ModbusRegisterCodec.EncodeBytes(odd);
    if (oddRegisters.Length != 2 || oddRegisters[1] != 0xCC00)
    {
        return (false, "odd encode wrong");
    }

    return (ModbusRegisterCodec.DecodeBytes(oddRegisters, 3).SequenceEqual(odd), "codec round-trips ok");
}

(bool, string) Test9_ModbusMatrixSpec()
{
    // 73 B map (axes byte, data ushort) -> 37 registers, valid readWrite spec
    var map = MapVariable43B();
    map.Data = new MatrixSection { DataType = ValueDataType.UShort };

    var spec = ModbusVariableSpecification.Create(
        "registerType=\"holdingRegister\";address=\"100\";direction=\"readWrite\";eventRef=\"poll\"",
        [new Qenex.QSuite.Variables.VariableEvents.PeriodicVarEvent { Name = "poll", Period = 100 }],
        map, requirePollEvent: true);

    if (spec.MatrixByteCount != 73 || spec.RegisterCount != 37)
    {
        return (false, $"expected 73 B / 37 registers, got {spec.MatrixByteCount} B / {spec.RegisterCount}");
    }

    // bit table rejected
    try
    {
        ModbusVariableSpecification.Create("registerType=\"coil\";address=\"0\";direction=\"write\"", null, map, false);
        return (false, "matrix in a bit table was not rejected");
    }
    catch (ArgumentException)
    {
    }

    // over the 123-register write limit rejected (124 registers = 248 B block)
    var big = new MatrixVariable { Name = "Big", Data = new MatrixSection { Count = 248 } };
    try
    {
        ModbusVariableSpecification.Create("address=\"0\";direction=\"write\"", null, big, false);
        return (false, "248 B write matrix was not rejected");
    }
    catch (ArgumentException)
    {
    }

    // ...but the same block is still readable (125-register read limit)
    var readSpec = ModbusVariableSpecification.Create(
        "address=\"0\";direction=\"read\";eventRef=\"poll\"",
        [new Qenex.QSuite.Variables.VariableEvents.PeriodicVarEvent { Name = "poll", Period = 100 }],
        big, requirePollEvent: true);

    return (readSpec.RegisterCount == 124, "matrix specification ok");
}

(bool, string) Test10_PendingWriteQueue()
{
    var variable = MapVariable43B();

    variable.EnqueuePendingWrite(new MatrixWriteRequest(13, [0x11]));
    variable.EnqueuePendingWrite(new MatrixWriteRequest(0, [0x22]));

    if (!variable.TryDequeuePendingWrite(out var first) || first.ByteOffset != 13 || first.Bytes[0] != 0x11)
    {
        return (false, "first dequeued request wrong");
    }

    if (!variable.TryDequeuePendingWrite(out var second) || second.ByteOffset != 0)
    {
        return (false, "second dequeued request wrong");
    }

    return (!variable.TryDequeuePendingWrite(out _), "pending write queue ok");
}

(bool, string) Test7_SetValue()
{
    var variable = MapVariable43B();

    try
    {
        variable.SetValue(new byte[42]);
        return (false, "wrong-length buffer was accepted");
    }
    catch (ArgumentException)
    {
        // expected
    }

    try
    {
        variable.SetValue("not bytes");
        return (false, "non-byte[] value was accepted");
    }
    catch (InvalidCastException)
    {
        // expected
    }

    var buffer = new byte[43];
    buffer[42] = 99;
    variable.SetValue(buffer);
    return (Math.Abs(variable.GetRawValue(MatrixSectionKind.Data, 29) - 99) > 0
        ? (false, "last data element read failed")
        : (true, "SetValue checks ok"));
}
