﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PyriteLib
{
	public class CubeMetadata
	{
		public bool[,,] CubeExists { get; set; }
		public Extent WorldBounds { get; set; }

		public Extent VirtualWorldBounds { get; set; }

		public XyzPoint SetSize { get; set; }

        public XyPoint TextureSetSize { get; set; }

        public int VertexCount { get; set; }

        public CubeMetadata(XyzPoint size)
		{
            SetSize = size;
			CubeExists = new bool[size.X, size.Y, size.Z];
		}
	}
}
