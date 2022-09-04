﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;

namespace STTech.BytesIO.Modbus
{
    public class WriteMultipleCoilRegistersRequest : ModbusRequest
    {
        [Description("写入地址")]
        public ushort WriteAddress { get; set; }

        [Description("写入数据")]
        public bool[] Data { get; set; } = new bool[] { false };

        public WriteMultipleCoilRegistersRequest() : base(FunctionCode.WriteMultipleCoilRegisters)
        {

        }
        public override byte[] GetBytes()
        {
            List<byte> bytes = new List<byte>();
            bytes.AddRange(BitConverter.GetBytes(WriteAddress).Reverse());
            bytes.AddRange(BitConverter.GetBytes((ushort)Data.Length).Reverse());

            var bytesArray = Data.Slice(8);
            var bits = bytesArray.Select(arr =>
            {
                int b = 0;
                foreach (var c in arr.Reverse())
                {
                    b = b << 1;
                    if (c) b++;
                }
                return (byte)b;
            });

            bytes.Add((byte)bits.Count());
            bytes.AddRange(bits);
            Payload = bytes.ToArray();
            return base.GetBytes();
        }
    }
}
