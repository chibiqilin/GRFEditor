using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media.Imaging;
using ErrorManager;
using GRF.Core;
using GRF.FileFormats;
using GRF.FileFormats.GatFormat;
using GRF.FileFormats.GndFormat;
using GRF.FileFormats.RswFormat;
using GRF.IO;
using GRF.Image;
using GRFEditor.ApplicationConfiguration;
using GrfToWpfBridge;
using Utilities;
using Utilities.Extension;

namespace GRFEditor.Tools.Map {
	public class MapEditor {
		private MapEditorState _state = new MapEditorState();
		public string OutputTexturePath { get; set; }
		public string InputTexturePath { get; set; }
		public string InputMapPath { get; set; }
		public string OutputMapPath { get; set; }
		[ThreadStatic]
		private static Random _threadRandom;

		private static Random GetRandom() {
			if (_threadRandom == null) {
				_threadRandom = new Random(unchecked(Environment.TickCount * 31 + System.Threading.Thread.CurrentThread.ManagedThreadId));
			}
			return _threadRandom;
		}

	
		public void Begin() {
			_state = new MapEditorState();
		}

		public void ValidatePaths() {
			InputMapPath = GrfEditorConfiguration.FlatMapsMakerInputMapsPath;
			OutputMapPath = GrfEditorConfiguration.FlatMapsMakerOutputMapsPath;
			OutputTexturePath = GrfEditorConfiguration.FlatMapsMakerOutputTexturesPath;
			InputTexturePath = GrfEditorConfiguration.FlatMapsMakerInputTexturesPath;

			if (OutputMapPath != null) {
				try {
					if (OutputMapPath.IsExtension(".grf")) {
						GrfPath.CreateDirectoryFromFile(OutputMapPath);
					}
					else {
						Directory.CreateDirectory(OutputMapPath);
					}
				}
				catch {
				}
			}
			
			Directory.CreateDirectory(InputMapPath);
			Directory.CreateDirectory(OutputTexturePath);
			Directory.CreateDirectory(InputTexturePath);

			string[] textureFiles = new string[] { "cw.bmp", "c-3.bmp", "c-2.bmp", "c-1.bmp", "cx.bmp", "c0.bmp", "c1.bmp", "c2.bmp", "c3.bmp", "c4.bmp", "c5.bmp", "c6.bmp" };

			foreach (string file in textureFiles) {
				if (!File.Exists(Path.Combine(GrfEditorConfiguration.FlatMapsMakerInputTexturesPath, file))) {
					using (Stream resFilestream = typeof(EditorMainWindow).Assembly.GetManifestResourceStream("GRFEditor.Resources." + file)) {
						if (resFilestream != null) {
							byte[] data = new byte[resFilestream.Length];
							resFilestream.Read(data, 0, data.Length);
							File.WriteAllBytes(Path.Combine(GrfEditorConfiguration.FlatMapsMakerInputTexturesPath, file), data);
						}
					}
				}
			}
		}

		public void Generate(string mapName, FileStream writer, object oLock, List<FileEntry> entries) {
			string gatFileName = Path.Combine(InputMapPath, mapName + ".gat");
			string rswFileName = Path.Combine(InputMapPath, mapName + ".rsw");
			string gndFileName = Path.Combine(InputMapPath, mapName + ".gnd");

			byte[] gatData = File.ReadAllBytes(gatFileName);
			byte[] rswData = File.ReadAllBytes(rswFileName);
			byte[] gndData = File.ReadAllBytes(gndFileName);

			_generate(mapName, gatData, rswData, gndData, writer, oLock, entries);
		}

		public void Generate(string mapName, byte[] gatData, byte[] rswData, byte[] gndData, FileStream writer, object oLock, List<FileEntry> entries) {
			_generate(mapName, gatData, rswData, gndData, writer, oLock, entries);
		}

		private void _generate(string mapName, byte[] gatData, byte[] rswData, byte[] gndData, FileStream writer, object oLock, List<FileEntry> entries) {
			float waterLevel = Rsw.WaterLevel(rswData);

			RswHeader rswHeader = new RswHeader(new ByteReader(rswData));

			Gat gat = _configGatFile(gatData, rswHeader, waterLevel);
			Gnd gnd = _configGndFile(gndData, gat);
			Rsw rsw = _configRswFile(rswData, mapName, gnd);

			gat.LoadedPath = "data\\" + mapName + ".gat";
			gnd.LoadedPath = "data\\" + mapName + ".gnd";
			rsw.LoadedPath = "data\\" + mapName + ".rsw";

			lock (_state.Lock) {
				_saveFile(gat, writer, oLock, entries);
				_saveFile(rsw, writer, oLock, entries);
				_saveFile(gnd, writer, oLock, entries);
			}
		}

		private void _saveFile(IWriteableFile obj, FileStream writer, object oLock, List<FileEntry> entries) {
			using (var mem = new MemoryStream()) {
				obj.Save(mem);
				var data = mem.ReadAllBytes();
				var compressed = Compression.Compress(data);
				var offset = (uint)writer.Position;
				writer.Write(compressed, 0, compressed.Length);

				if (compressed.Length % 8 != 0) {
					writer.Write(new byte[8 - compressed.Length % 8], 0, 8 - compressed.Length % 8);
				}

				var entry = FileEntry.CreateBufferedEntry(writer.Name, "data\\" + Path.GetFileName(obj.LoadedPath), offset, compressed.Length, (int)(writer.Position - offset), data.Length);

				lock (oLock) {
					entries.Add(entry);
				}
			}
		}

		private Gnd _configGndFile(byte[] gndData, Gat gat) {
			Gnd gnd = new Gnd(gndData);

			if (gnd.Header.Version >= 1.7) {
				// Doesn't like new maps very much
				gnd.Header.SetVersion(1, 7);
			}

			if (GrfEditorConfiguration.FlattenGround) {
				gnd.SetCubesHeight(0);
			}

			if (GrfEditorConfiguration.RemoveAllLighting) {
				gnd.RemoveLightmaps();
			}

			if (GrfEditorConfiguration.UseCustomTextures) {
				if (GrfEditorConfiguration.TextureOriginal) {
					//gnd.ResetTextures();
				}
				else {
					gnd.ResetTextures();
				}

				if (GrfEditorConfiguration.FlattenGround) {
					gnd.RemoveAllTiles();
				}

				_setCubesAndTiles(gnd, gat);
			}

			return gnd;
		}


		[ThreadStatic] 
		private static Random _local; 
		
		private void _setCubesAndTiles(Gnd gnd, Gat gat) {
			string id = GrfEditorConfiguration.FlatMapsMakerId;

			if (GrfEditorConfiguration.TextureWalls) {
				gnd.AddTexture(id + "cw.bmp");

				try {
					if (!_state.WallTextureCopied) {
						_state.WallTextureCopied = true;
						File.Delete(Path.Combine(OutputTexturePath, id + "cw.bmp"));
						File.Copy(Path.Combine(InputTexturePath, "cw.bmp"), Path.Combine(OutputTexturePath, id + "cw.bmp"));
					}
				}
				catch {
				}
			}

			int offset;
			int[] cellIndexes = new int[4];
			int[] cellTypes = new int[4];
			//string[] cellTypesString = new string[4];
			string[] cellTextureFiles = new string[4];

			Tile tile;
			Cube cube;
			Cell gatCell;

			// For each cube...
			for (int y = 0; y < gnd.Header.Height; y++) {
				for (int x = 0; x < gnd.Header.Width; x++) {
					StringBuilder b = new StringBuilder();
					
					// Find the texture for the tile
					int cellIndex = 2 * (x + 2 * y * gnd.Header.Width);
					cellIndexes[0] = cellIndex;
					cellIndexes[1] = cellIndex + 1;
					cellIndexes[2] = cellIndex + gnd.Header.Width * 2;
					cellIndexes[3] = cellIndexes[2] + 1;

					b.Append(id);

					for (int i = 0; i < 4; i++) {
						gatCell = gat.Cells[cellIndexes[i]];

						if (gatCell.IsWater == true) {
							cellTypes[i] = -1;

							// Shoreline check
							bool n = _getGatIsWater(gat, cellIndexes[i] - gnd.Header.Width * 2);
							bool e = _getGatIsWater(gat, cellIndexes[i] + 1);
							bool s = _getGatIsWater(gat, cellIndexes[i] + gnd.Header.Width * 2);
							bool w = _getGatIsWater(gat, cellIndexes[i] - 1);

							int bitmask = 0;
							if (!n) bitmask |= 1;
							if (!e) bitmask |= 2;
							if (!s) bitmask |= 4;
							if (!w) bitmask |= 8;

							// Diagonals: if both adjacent cardinal directions are water,
							//if (n && e && !_getGatIsWater(gat, cellIndexes[i] - gnd.Header.Width * 2 + 1)) // NE
							//	bitmask |= 16;
							//if (s && e && !_getGatIsWater(gat, cellIndexes[i] + gnd.Header.Width * 2 + 1)) // SE
							//	bitmask |= 32;
							//if (s && w && !_getGatIsWater(gat, cellIndexes[i] + gnd.Header.Width * 2 - 1)) // SW
							//	bitmask |= 64;
							//if (n && w && !_getGatIsWater(gat, cellIndexes[i] - gnd.Header.Width * 2 - 1)) // NW
							//	bitmask |= 128;
							
							string shoreline;

							if (bitmask == 0) {
								// Check for variants like c-1_0_0.bmp, c-1_0_1.bmp...
								string[] shorelineVariants = Directory.GetFiles(InputTexturePath, "c-1_0_*.bmp");

								if (shorelineVariants.Length > 0) {
									// Prefer 0 variant with 80% chance
									string preferred = Path.Combine(InputTexturePath, "c-1_0_0.bmp");
									if (File.Exists(preferred) && GetRandom().NextDouble() < 0.8) {
										shoreline = "c-1_0_0";
									} else {
										shoreline = Path.GetFileNameWithoutExtension(shorelineVariants[GetRandom().Next(shorelineVariants.Length)]);
									}
								} else {
									shoreline = "c-1_0";
								}
							} else {
								shoreline = $"c-1_{bitmask}";
							}

							// Set texture name
							cellTextureFiles[i] = shoreline;
							b.Append(shoreline);
						}
						else if (gatCell.IsInnerGutterLine == true) {
							cellTypes[i] = -3;
							b.Append("c-3");
						}
						else if (gatCell.IsOutterGutterLine == true) {
							cellTypes[i] = -2;
							b.Append("c-2");
						}
						else {
							switch(gatCell.Type) {
								case GatType.Weird0:
									cellTypes[i] = (int)GatType.Walkable;
									break;
								case GatType.Weird1:
									cellTypes[i] = (int)GatType.NoWalkable;
									break;
								case GatType.Weird2:
									cellTypes[i] = (int)GatType.NoWalkableNoSnipable;
									break;
								case GatType.Weird3:
									cellTypes[i] = (int)GatType.Walkable2;
									break;
								case GatType.Weird4:
									cellTypes[i] = (int)GatType.Unknown;
									break;
								case GatType.Weird5:
									cellTypes[i] = (int)GatType.NoWalkableSnipable;
									break;
								case GatType.Weird6:
									cellTypes[i] = (int)GatType.Walkable3;
									break;
								case GatType.Weird7:
									cellTypes[i] = (int)GatType.NoWalkable;
									break;
								case GatType.Weird8:
									cellTypes[i] = (int)GatType.NoWalkable;
									break;
								case GatType.Weird9:
									cellTypes[i] = (int)GatType.NoWalkable;
									break;
								default:
									cellTypes[i] = (int)gatCell.Type;
									break;
							}
							//b.Append(Path.GetFileNameWithoutExtension(_getVariantTextureName(cellTypes[i])));
							string baseName = "c" + cellTypes[i];
							string[] variants = Directory.GetFiles(InputTexturePath, baseName + "_*.bmp");
							string selected;

							if (variants.Length > 0) {
								string preferred = Path.Combine(InputTexturePath, baseName + "_0.bmp");
								bool hasZero = File.Exists(preferred);

								if (hasZero && GetRandom().NextDouble() < 0.8) {
									selected = Path.GetFileNameWithoutExtension(preferred);
								}
								else {
									selected = Path.GetFileNameWithoutExtension(variants[GetRandom().Next(variants.Length)]);
								}
							}
							else if (File.Exists(Path.Combine(InputTexturePath, baseName + ".bmp"))) {
								selected = baseName;
							}
							else {
								selected = "cx";
							}

							cellTextureFiles[i] = selected;  // <-- Store the filename (without .bmp)
							b.Append(selected);

						}
					}
					b.Append(".bmp");

					string textureName = b.ToString();
					b = new StringBuilder();
					
					// Check if this full tile already exists as a prebuilt file in the quadrant folder
					//string prebuiltPath = Path.Combine(InputTexturePath, "quadrants", textureName);
					//string targetPath = Path.Combine(OutputTexturePath, textureName);

					//if (!_state.OutputTexturePaths.Contains(targetPath) && File.Exists(prebuiltPath)) {
					//	try {
					//		File.Copy(prebuiltPath, targetPath, overwrite: true);
					//		lock (_state.Lock) {
					//			_state.OutputTexturePaths.Add(targetPath);
					//		}
					//	}
					//	catch (Exception ex) {
					//		ErrorHandler.HandleException(ex);
					//	}
					//}


					lock (_state.Lock) {
						if (_state.OutputTexturePaths == null) {
							_state.OutputTexturePaths = new HashSet<string>(Directory.GetFiles(OutputTexturePath, "*.bmp"));
						}
					}

					if (gnd.GetTextureIndex(textureName) < 0) {
						var texturePath = Path.Combine(OutputTexturePath, textureName);

						lock (_state.Lock) {
							if (!_state.OutputTexturePaths.Contains(texturePath)) {
								GenerateTexture(textureName, cellTextureFiles);
								_state.OutputTexturePaths.Add(texturePath);
							}
						}

						gnd.AddTexture(textureName);
					}

					// If the ground is flattened, a new tile must be created
					if (GrfEditorConfiguration.FlattenGround) {
						tile = new Tile(gnd.GetTextureIndex(textureName));

						gnd.Tiles.Add(tile);
						offset = y * gnd.Header.Width + x;

						cube = gnd.Cubes[offset];
						cube.TileUp = gnd.Tiles.Count - 1;
						cube.TileSide = -1;
						cube.TileFront = -1;
					}
					// The old tile is kept
					else {
						cube = gnd.Cubes[y * gnd.Header.Width + x];

						if (cube.TileUp > -1) {
							tile = gnd.Tiles[cube.TileUp];
							tile.TextureIndex = gnd.GetTextureIndex(textureName);
							tile.ResetTextureUv();
						}

						if (GrfEditorConfiguration.TextureWalls) {
							if (cube.TileSide > -1) {
								tile = gnd.Tiles[cube.TileSide];
								tile.TextureIndex = gnd.GetTextureIndex(id + "cw.bmp");
								tile.ResetTextureUv();
							}

							if (cube.TileFront > -1) {
								tile = gnd.Tiles[cube.TileFront];
								tile.TextureIndex = gnd.GetTextureIndex(id + "cw.bmp");
								tile.ResetTextureUv();
							}
						}
					}
				}
			}

			if (GrfEditorConfiguration.StickGatCellsToGround) {
				gat.Adjust(gnd);
			}
		}
		
		private bool _getGatIsWater(Gat gat, int index) {
			if (index >= gat.Cells.Length || index < 0)
				return true;// treat out-of-bounds as water
			return gat.Cells[index].IsWater ?? false;
		}

		public void GenerateTexture(string textureName, IList<string> cellTextureFiles) {
			var data = new byte[12288];
			GrfImage img = new GrfImage(data, 64, 64, GrfImageType.Bgr24);

			img.SetPixels(32, 0, 32, 32, _getPixels(cellTextureFiles[0] + ".bmp"));
			img.SetPixels(0, 0, 32, 32, _getPixels(cellTextureFiles[1] + ".bmp"));
			img.SetPixels(32, 32, 32, 32, _getPixels(cellTextureFiles[2] + ".bmp"));
			img.SetPixels(0, 32, 32, 32, _getPixels(cellTextureFiles[3] + ".bmp"));

			img.Save(Path.Combine(OutputTexturePath, textureName));

			//WriteableBitmap bit = new WriteableBitmap(64, 64, 96, 96, PixelFormats.Bgr24, null);
			//bit.WritePixels(new Int32Rect(32, 0, 32, 32), _getPixels(cellTypes[0]), 32 * PixelFormats.Bgr24.BitsPerPixel / 8, 0);
			//bit.WritePixels(new Int32Rect(0, 0, 32, 32), _getPixels(cellTypes[1]), 32 * PixelFormats.Bgr24.BitsPerPixel / 8, 0);
			//bit.WritePixels(new Int32Rect(32, 32, 32, 32), _getPixels(cellTypes[2]), 32 * PixelFormats.Bgr24.BitsPerPixel / 8, 0);
			//bit.WritePixels(new Int32Rect(0, 32, 32, 32), _getPixels(cellTypes[3]), 32 * PixelFormats.Bgr24.BitsPerPixel / 8, 0);
			//
			//try {
			//	using (FileStream stream = new FileStream(Path.Combine(OutputTexturePath, textureName), FileMode.Create)) {
			//		BmpBitmapEncoder encoder = new BmpBitmapEncoder();
			//		encoder.Frames.Add(BitmapFrame.Create(bit));
			//		encoder.Save(stream);
			//		stream.Close();
			//	}
			//}
			//catch { }
		}

		private byte[] _getPixels(string textureFileName) {
			if (_state.BufferedImages.ContainsKey(textureFileName.GetHashCode()))
				return _state.BufferedImages[textureFileName.GetHashCode()];

			string filePath = Path.Combine(InputTexturePath, textureFileName);
			
			// Fallback for shoreline: c-1_3.bmp → c-1.bmp
			if (!File.Exists(filePath) && textureFileName.StartsWith("c-1_")) {
				filePath = Path.Combine(InputTexturePath, "c-1.bmp");
			}

			// Fallback for other variants: c0_2.bmp → c0.bmp
			else if (!File.Exists(filePath) && textureFileName.StartsWith("c") && textureFileName.Contains("_")) {
				string baseName = textureFileName.Split('_')[0];
				filePath = Path.Combine(InputTexturePath, baseName + ".bmp");
			}
			
			// Final fallback
			if (!File.Exists(filePath)) {
				filePath = Path.Combine(InputTexturePath, "cx.bmp");
			}
	
			BmpBitmapDecoder decoder = new BmpBitmapDecoder(new Uri(filePath), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.Default);
			var frame = decoder.Frames[0];

			byte[] pixels = new byte[frame.PixelHeight * frame.PixelWidth * frame.Format.BitsPerPixel / 8];
			frame.CopyPixels(pixels, frame.PixelWidth * frame.Format.BitsPerPixel / 8, 0);

			_state.BufferedImages[textureFileName.GetHashCode()] = pixels;
			return pixels;
		}


		private Rsw _configRswFile(byte[] rswData, string mapName, Gnd gnd) {
			Rsw rsw;

			if (GrfEditorConfiguration.FlattenGround) {
				rsw = Rsw.CreateEmpty(mapName);
				rsw.Header.SetVersion(1, 9);

				// Already reseted, no need for GrfEditorConfiguration.ResetGlobalLighting
				if (!GrfEditorConfiguration.ResetGlobalLighting) {
					var rsw2 = new Rsw(rswData);
					rsw.Light.Longitude = rsw2.Light.Longitude;
					rsw.Light.Latitude = rsw2.Light.Latitude;
					rsw.Light.DiffuseRed = rsw2.Light.DiffuseRed;
					rsw.Light.DiffuseGreen = rsw2.Light.DiffuseGreen;
					rsw.Light.DiffuseBlue = rsw2.Light.DiffuseBlue;
					rsw.Light.AmbientRed = rsw2.Light.AmbientRed;
					rsw.Light.AmbientGreen = rsw2.Light.AmbientGreen;
					rsw.Light.AmbientBlue = rsw2.Light.AmbientBlue;
				}
			}
			else {
				rsw = new Rsw(rswData);

				if (GrfEditorConfiguration.ResetGlobalLighting) {
					rsw.ResetLight();
				}

				if (GrfEditorConfiguration.RemoveAllObjects) {
					rsw.RemoveObjects();
				}

				if (GrfEditorConfiguration.RemoveWater) {
					rsw.Water.Reset();
					rsw.Water.Level = -(gnd.Cubes.Min(p => p.BottomLeft) - 100f);
				}
			}

			return rsw;
		}

		private Gat _configGatFile(byte[] gatData, RswHeader rswHeader, float waterLevel) {
			Gat gat = new Gat(gatData);

			if (rswHeader.Version < 2.6) {
				gat.IdentifyWaterCells(waterLevel);
			}

			if (GrfEditorConfiguration.FlattenGround)
				gat.SetCellsHeight(0);

			if (GrfEditorConfiguration.ShowGutterLines)
				gat.IdentifyGutterLines();

			return gat;
		}

		public void ClearTextures() {
			try {
				foreach (string file in Directory.GetFiles(GrfEditorConfiguration.FlatMapsMakerOutputTexturesPath, "*")) {
					string id = GrfEditorConfiguration.FlatMapsMakerId;

					if (id == "")
						GrfPath.Delete(file);
					else {
						if (Path.GetFileName(file).StartsWith(id)) {
							GrfPath.Delete(file);
						}
					}
				}
			}
			catch (Exception err) {
				ErrorHandler.HandleException(err);
			}
		}
		
		private string _getVariantTextureName(int cellType) {
			string baseName = "c" + cellType;
			string basePath = Path.Combine(InputTexturePath, baseName + ".bmp");
			string[] variants = Directory.GetFiles(InputTexturePath, baseName + "_*.bmp");

			if (variants.Length > 0) {
				string chosen = Path.GetFileName(variants[GetRandom().Next(variants.Length)]);
				return chosen;
			}
			else if (File.Exists(basePath)) {
				return baseName + ".bmp";
			}
			else {
				return "cx.bmp";
			}
		}

		#region Nested type: MapEditorState

		private class MapEditorState {
			public readonly TkDictionary<int, byte[]> BufferedImages = new TkDictionary<int, byte[]>();
			//public readonly TkDictionary<int, Tile> BufferedTiles = new TkDictionary<int, Tile>();
			public HashSet<string> OutputTexturePaths;
			public bool WallTextureCopied;
			public readonly object Lock = new object();
			public ReadOnlyCollection<string> OldTextures { get; set; }
		}

		#endregion
	}
}