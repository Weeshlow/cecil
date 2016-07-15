//
// Author:
//   Jb Evain (jbevain@gmail.com)
//
// Copyright (c) 2008 - 2015 Jb Evain
// Copyright (c) 2008 - 2011 Novell, Inc.
//
// Licensed under the MIT/X11 license.
//

using System;
using System.Collections.Generic;
using System.Text;

using Mono.Cecil.PE;

using RVA = System.UInt32;

#if !READ_ONLY

namespace Mono.Cecil.Metadata {

	sealed class TableHeapBuffer : HeapBuffer {

		readonly ModuleDefinition module;
		readonly MetadataBuilder metadata;

		internal MetadataTable [] tables = new MetadataTable [Mixin.TableCount];

		bool large_string;
		bool large_blob;
		bool large_guid;

		readonly int [] coded_index_sizes = new int [Mixin.CodedIndexCount];
		readonly Func<Table, int> counter;

		public override bool IsEmpty {
			get { return false; }
		}

		public TableHeapBuffer (ModuleDefinition module, MetadataBuilder metadata)
			: base (24)
		{
			this.module = module;
			this.metadata = metadata;
			this.counter = GetTableLength;
		}

		int GetTableLength (Table table)
		{
			var md_table = tables [(int) table];
			return md_table != null ? md_table.Length : 0;
		}

		public TTable GetTable<TTable> (Table table) where TTable : MetadataTable, new ()
		{
			var md_table = (TTable) tables [(int) table];
			if (md_table != null)
				return md_table;

			md_table = new TTable ();
			tables [(int) table] = md_table;
			return md_table;
		}

		public void WriteBySize (uint value, int size)
		{
			if (size == 4)
				WriteUInt32 (value);
			else
				WriteUInt16 ((ushort) value);
		}

		public void WriteBySize (uint value, bool large)
		{
			if (large)
				WriteUInt32 (value);
			else
				WriteUInt16 ((ushort) value);
		}

		public void WriteString (uint @string)
		{
			WriteBySize (@string, large_string);
		}

		public void WriteBlob (uint blob)
		{
			WriteBySize (blob, large_blob);
		}

		public void WriteGuid (uint guid)
		{
			WriteBySize (guid, large_guid);
		}

		public void WriteRID (uint rid, Table table)
		{
			var md_table = tables [(int) table];
			WriteBySize (rid, md_table == null ? false : md_table.IsLarge);
		}

		int GetCodedIndexSize (CodedIndex coded_index)
		{
			var index = (int) coded_index;
			var size = coded_index_sizes [index];
			if (size != 0)
				return size;

			return coded_index_sizes [index] = coded_index.GetSize (counter);
		}

		public void WriteCodedRID (uint rid, CodedIndex coded_index)
		{
			WriteBySize (rid, GetCodedIndexSize (coded_index));
		}

		public void WriteTableHeap ()
		{
			WriteUInt32 (0);					// Reserved
			WriteByte (GetTableHeapVersion ());	// MajorVersion
			WriteByte (0);						// MinorVersion
			WriteByte (GetHeapSizes ());		// HeapSizes
			WriteByte (10);						// Reserved2
			WriteUInt64 (GetValid ());			// Valid
			WriteUInt64 (0x0016003301fa00);		// Sorted

			WriteRowCount ();
			WriteTables ();
		}

		void WriteRowCount ()
		{
			for (int i = 0; i < tables.Length; i++) {
				var table = tables [i];
				if (table == null || table.Length == 0)
					continue;

				WriteUInt32 ((uint) table.Length);
			}
		}

		void WriteTables ()
		{
			for (int i = 0; i < tables.Length; i++) {
				var table = tables [i];
				if (table == null || table.Length == 0)
					continue;

				table.Write (this);
			}
		}

		ulong GetValid ()
		{
			ulong valid = 0;

			for (int i = 0; i < tables.Length; i++) {
				var table = tables [i];
				if (table == null || table.Length == 0)
					continue;

				table.Sort ();
				valid |= (1UL << i);
			}

			return valid;
		}

		byte GetHeapSizes ()
		{
			byte heap_sizes = 0;

			if (metadata.string_heap.IsLarge) {
				large_string = true;
				heap_sizes |= 0x01;
			}

			if (metadata.guid_heap.IsLarge) {
				large_guid = true;
				heap_sizes |= 0x02;
			}

			if (metadata.blob_heap.IsLarge) {
				large_blob = true;
				heap_sizes |= 0x04;
			}

			return heap_sizes;
		}

		byte GetTableHeapVersion ()
		{
			switch (module.Runtime) {
			case TargetRuntime.Net_1_0:
			case TargetRuntime.Net_1_1:
				return 1;
			default:
				return 2;
			}
		}

		public void FixupData (RVA data_rva)
		{
			var table = GetTable<FieldRVATable> (Table.FieldRVA);
			if (table.length == 0)
				return;

			var field_idx_size = GetTable<FieldTable> (Table.Field).IsLarge ? 4 : 2;
			var previous = this.position;

			base.position = table.position;
			for (int i = 0; i < table.length; i++) {
				var rva = ReadUInt32 ();
				base.position -= 4;
				WriteUInt32 (rva + data_rva);
				base.position += field_idx_size;
			}

			base.position = previous;
		}
	}

	sealed class ResourceBuffer : ByteBuffer {

		public ResourceBuffer ()
			: base (0)
		{
		}

		public uint AddResource (byte [] resource)
		{
			var offset = (uint) this.position;
			WriteInt32 (resource.Length);
			WriteBytes (resource);
			return offset;
		}
	}

	sealed class DataBuffer : ByteBuffer {

		public DataBuffer ()
			: base (0)
		{
		}

		public RVA AddData (byte [] data)
		{
			var rva = (RVA) position;
			WriteBytes (data);
			return rva;
		}
	}

	abstract class HeapBuffer : ByteBuffer {

		public bool IsLarge {
			get { return base.length > 65535; }
		}

		public abstract bool IsEmpty { get; }

		protected HeapBuffer (int length)
			: base (length)
		{
		}
	}

	sealed class GuidHeapBuffer : HeapBuffer {

		readonly Dictionary<Guid, uint> guids = new Dictionary<Guid, uint> ();

		public override bool IsEmpty {
			get { return length == 0; }
		}

		public GuidHeapBuffer ()
			: base (16)
		{
		}

		public uint GetGuidIndex (Guid guid)
		{
			uint index;
			if (guids.TryGetValue (guid, out index))
				return index;

			index = (uint) guids.Count + 1;
			WriteGuid (guid);
			guids.Add (guid, index);
			return index;
		}

		void WriteGuid (Guid guid)
		{
			WriteBytes (guid.ToByteArray ());
		}
	}

	class StringHeapBuffer : HeapBuffer {

		readonly Dictionary<string, uint> strings = new Dictionary<string, uint> (StringComparer.Ordinal);

		public sealed override bool IsEmpty {
			get { return length <= 1; }
		}

		public StringHeapBuffer ()
			: base (1)
		{
			WriteByte (0);
		}

		public uint GetStringIndex (string @string)
		{
			uint index;
			if (strings.TryGetValue (@string, out index))
				return index;

			index = (uint) base.position;
			WriteString (@string);
			strings.Add (@string, index);
			return index;
		}

		protected virtual void WriteString (string @string)
		{
			WriteBytes (Encoding.UTF8.GetBytes (@string));
			WriteByte (0);
		}
	}

	sealed class BlobHeapBuffer : HeapBuffer {

		readonly Dictionary<ByteBuffer, uint> blobs = new Dictionary<ByteBuffer, uint> (new ByteBufferEqualityComparer ());

		public override bool IsEmpty {
			get { return length <= 1; }
		}

		public BlobHeapBuffer ()
			: base (1)
		{
			WriteByte (0);
		}

		public uint GetBlobIndex (ByteBuffer blob)
		{
			uint index;
			if (blobs.TryGetValue (blob, out index))
				return index;

			index = (uint) base.position;
			WriteBlob (blob);
			blobs.Add (blob, index);
			return index;
		}

		void WriteBlob (ByteBuffer blob)
		{
			WriteCompressedUInt32 ((uint) blob.length);
			WriteBytes (blob);
		}
	}

	sealed class UserStringHeapBuffer : StringHeapBuffer {

		protected override void WriteString (string @string)
		{
			WriteCompressedUInt32 ((uint) @string.Length * 2 + 1);

			byte special = 0;

			for (int i = 0; i < @string.Length; i++) {
				var @char = @string [i];
				WriteUInt16 (@char);

				if (special == 1)
					continue;

				if (@char < 0x20 || @char > 0x7e) {
					if (@char > 0x7e
						|| (@char >= 0x01 && @char <= 0x08)
						|| (@char >= 0x0e && @char <= 0x1f)
						|| @char == 0x27
						|| @char == 0x2d) {

						special = 1;
					}
				}
			}

			WriteByte (special);
		}
	}

	sealed class PdbHeapBuffer : HeapBuffer {

		public override bool IsEmpty {
			get { return false; }
		}

		public PdbHeapBuffer ()
			: base (0)
		{
		}
	}
}

#endif
