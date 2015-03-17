﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CuberLib.Types;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace CuberLib
{
    public class Obj
    {
		const int NUMCORES = 7;

        public List<Vertex> VertexList;
        public List<Face> FaceList;
        public List<TextureVertex> TextureList;

        public Extent Size { get; set; }

        private string mtl;

		/// <summary>
		/// Parse and load an OBJ file into memory.  Will consume memory
		/// at aproximately 120% the size of the file.
		/// </summary>
		/// <param name="path">path to obj file on disk</param>
		/// <param name="linesProcessedCallback">callback for status updates</param>
        public void LoadObj(string path, Action<int> linesProcessedCallback)
        {
            VertexList = new List<Vertex>();
            FaceList = new List<Face>();
            TextureList = new List<TextureVertex>();

            var input = File.ReadLines(path);

            int linesProcessed = 0;
                                
            foreach (string line in input)
            {
                processLine(line);

                // Handle a callback for a status update
                linesProcessed++;
                if (linesProcessedCallback != null && linesProcessed % 1000 == 0)
                    linesProcessedCallback(linesProcessed);
            }

            if (linesProcessedCallback != null)
                linesProcessedCallback(linesProcessed);

            updateSize();                              
        }

		/// <summary>
		/// Write a single "cube".
		/// Pending addition of Z-axis so that they are actually cubes.
		/// </summary>
		/// <param name="path">Output path</param>
		/// <param name="gridHeight">Y size of grid</param>
		/// <param name="gridWidth">X size of grid</param>
		/// <param name="tileX">Zero based X index of tile</param>
		/// <param name="tileY">Zero based Y index of tile</param>
        public int WriteObjGridTile(string path, int gridHeight, int gridWidth, int gridDepth, int tileX, int tileY, int tileZ, string mtlOverride)
        {
            double tileHeight = Size.YSize / gridHeight;
            double tileWidth = Size.XSize / gridWidth;
			double tileDepth = Size.ZSize / gridDepth;

			double yOffset = tileHeight * tileY;
            double xOffset = tileWidth * tileX;
			double zOffset = tileDepth * tileZ;

			Extent newSize = new Extent
            {
                XMin = Size.XMin + xOffset,
                YMin = Size.YMin + yOffset,
				ZMin = Size.ZMin + zOffset,				
                XMax = Size.XMin + xOffset + tileWidth,
                YMax = Size.YMin + yOffset + tileHeight,
				ZMax = Size.ZMin + zOffset + tileDepth
			};

			return WriteObj(path, newSize, mtlOverride);
        }

		/// <summary>
		/// Writes an OBJ uses vertices and faces contained within the provided boundries.
		/// Typically used by WriteObjGridTile(...)
		/// Returns number of vertices written, or 0 if nothing was written.
		/// </summary>
		public int WriteObj(string path, Extent boundries, string mtlOverride)
        {
            if (!Directory.Exists(Path.GetDirectoryName(path))) Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path)) File.Delete(path);

            // Build the chunk
            List<Vertex> chunkVertexList;
            List<Face> chunkFaceList;
            List<TextureVertex> chunkTextureList;
            HashSet<Face> chunkFaceHashSet;

            // Revert all vertices in case we previously changed their indexes
            FaceList.ForEach(f => f.RevertVertices());

            // Get all faces in this cube
            chunkFaceList = FaceList.Where(v => v.InExtent(boundries, VertexList)).ToList();

            // Build a list of vertices indexes needed for these faces
            List<int> requiredVertices = null;
            List<int> requiredTextureVertices = null;

            var tv = Task.Run(() => { requiredVertices = chunkFaceList.SelectMany(f => f.VertexIndexList).Distinct().ToList(); });
            var ttv = Task.Run(() => { requiredTextureVertices = chunkFaceList.SelectMany(f => f.TextureVertexIndexList).Distinct().ToList(); });

            tv.Wait();

            // Abort if we would be writing an empty file
            // no need to wait on texture vertices;
            if (!requiredVertices.Any())
            {
                return 0;
            }
            ttv.Wait();

            Console.WriteLine("{0} vertices and {1} texture vertices", requiredVertices.Count, requiredTextureVertices.Count);

            WriteObjFormattedFile(path, mtlOverride, chunkFaceList, requiredVertices, requiredTextureVertices);
            WriteEboFormattedFile(path, mtlOverride, chunkFaceList, requiredVertices, requiredTextureVertices);

            return requiredVertices.Count();
        }

        private void WriteObjFormattedFile(string path, string mtlOverride, List<Face> chunkFaceList, List<int> requiredVertices, List<int> requiredTextureVertices)
        {
            using (var outStream = File.OpenWrite(path))
            using (var writer = new StreamWriter(outStream))
            {

                // Write some header data
                writer.WriteLine("# Generated by Cuber");

                if (!string.IsNullOrEmpty(mtlOverride))
                {
                    writer.WriteLine("mtllib " + mtlOverride);
                }
                else if (!string.IsNullOrEmpty(mtl))
                {
                    writer.WriteLine("mtllib " + mtl);
                }

                // Write each vertex and update faces				
                int newVertexIndex = 0;

                Parallel.ForEach(requiredVertices, new ParallelOptions { MaxDegreeOfParallelism = NUMCORES }, i =>
                {
                    Vertex moving = VertexList[i - 1];
                    int newIndex = WriteVertexWithNewIndex(moving, ref newVertexIndex, writer);

                    var facesRequiringUpdate = chunkFaceList.Where(f => f.VertexIndexList.Contains(i));
                    foreach (var face in facesRequiringUpdate) face.UpdateVertexIndex(moving.Index, newIndex);
                });


                //Write each texture vertex and update faces
                int newTextureVertexIndex = 0;

                Parallel.ForEach(requiredTextureVertices, new ParallelOptions { MaxDegreeOfParallelism = NUMCORES }, i =>
                {
                    TextureVertex moving = TextureList[i - 1];
                    int newIndex = WriteVertexWithNewIndex(moving, ref newTextureVertexIndex, writer);

                    var facesRequiringUpdate = chunkFaceList.Where(f => f.TextureVertexIndexList.Contains(i));
                    foreach (var face in facesRequiringUpdate) face.UpdateTextureVertexIndex(moving.Index, newIndex);
                });

                // Write the faces
                chunkFaceList.ForEach(f => writer.WriteLine(f));
            }
        }

        private void WriteEboFormattedFile(string path, string mtlOverride, List<Face> chunkFaceList, List<int> requiredVertices, List<int> requiredTextureVertices)
        {
            using (var outStream = File.OpenWrite(path + ".ebo"))
            using (var writer = new BinaryWriter(outStream))
            {
                foreach (var f in chunkFaceList)
                {
                    writer.Write('F');

                    for (int i = 0; i < f.VertexIndexList.Length; i++)
                    {
                        writer.Write(VertexList[f.VertexIndexList[i]].X);
                        writer.Write(VertexList[f.VertexIndexList[i]].Y);
                        writer.Write(VertexList[f.VertexIndexList[i]].Z);
                        writer.Write(TextureList[f.TextureVertexIndexList[i]].X);
                        writer.Write(TextureList[f.TextureVertexIndexList[i]].Y);
                    }
                }
            }
        }


        /// <summary>
        /// Helper to make determining the index of the written vertex
        /// and the stream output thread safe.  
        /// We block on writing the line, and incrementing the index.
        /// Has no real performance impact as most of the time is spent traversing arrays.
        /// </summary>
        [MethodImpl(MethodImplOptions.Synchronized)]
		private int WriteVertexWithNewIndex<T>(T item, ref int index, StreamWriter writer)
        {
			writer.WriteLine(item);
			index++;
			return index;
        }

		/// <summary>
		/// Sets our global object size with an extent object
		/// </summary>
        private void updateSize()
        {
            Size = new Extent
            {
                XMax = VertexList.Max(v => v.X),
                XMin = VertexList.Min(v => v.X),
                YMax = VertexList.Max(v => v.Y),
                YMin = VertexList.Min(v => v.Y),
                ZMax = VertexList.Max(v => v.Z),
                ZMin = VertexList.Min(v => v.Z)
            };
        }

		/// <summary>
		/// Parses and loads a line from an OBJ file.
		/// Currently only supports V, VT, F and MTLLIB prefixes
		/// </summary>		
        private void processLine(string line)
        {
            string[] parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length > 0)
            {
                switch (parts[0])
                {
                    case "mtllib":
                        mtl = parts[1];
                        break;
                    case "v":
                        Vertex v = new Vertex();
                        v.LoadFromStringArray(parts);
                        VertexList.Add(v);
                        v.Index = VertexList.Count();
                        break;
                    case "f":
                        Face f = new Face();
                        f.LoadFromStringArray(parts);
                        FaceList.Add(f);
                        break;
                    case "vt":
                        TextureVertex vt = new TextureVertex();
                        vt.LoadFromStringArray(parts);
                        TextureList.Add(vt);
                        vt.Index = TextureList.Count();
                        break;

                }
            }
        }

    }
}
