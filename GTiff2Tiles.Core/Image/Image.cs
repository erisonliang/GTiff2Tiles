﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NetVips;
// ReSharper disable AccessToDisposedClosure

namespace GTiff2Tiles.Core.Image
{
    /// <summary>
    /// Class for creating raster tiles.
    /// </summary>
    public class Image
    {
        #region Properties

        /// <summary>
        /// Image's width.
        /// </summary>
        private int RasterXSize { get; set; }

        /// <summary>
        /// Image's height.
        /// </summary>
        private int RasterYSize { get; set; }

        /// <summary>
        /// Array from Gdal, contains coordinates and pixel resolutions.
        /// </summary>
        private double[] GeoTransform { get; set; }

        /// <summary>
        /// Dictionary with min/max tile numbers for each zoom level.
        /// </summary>
        private Dictionary<int, int[]> MinMax { get; } = new Dictionary<int, int[]>();

        /// <summary>
        /// Input GeoTiff.
        /// </summary>
        private FileInfo InputFileInfo { get; set; }

        /// <summary>
        /// Output directory.
        /// </summary>
        private DirectoryInfo OutputDirectoryInfo { get; }

        /// <summary>
        /// Minimum cropped zoom.
        /// </summary>
        private int MinZ { get; }

        /// <summary>
        /// Maximum cropped zoom.
        /// </summary>
        private int MaxZ { get; }

        #endregion

        #region Constructor

        /// <summary>
        /// Creates new <see cref="Image"/> object.
        /// </summary>
        /// <param name="inputFileInfo">Input GeoTiff.</param>
        /// <param name="outputDirectoryInfo">Output directory.</param>
        /// <param name="minZ">Minimum cropped zoom.</param>
        /// <param name="maxZ">Maximum cropped zoom.</param>
        public Image(FileInfo inputFileInfo, DirectoryInfo outputDirectoryInfo, int minZ, int maxZ) =>
            (InputFileInfo, OutputDirectoryInfo, MinZ, MaxZ) =
            (inputFileInfo, outputDirectoryInfo, minZ, maxZ);

        #endregion

        #region Methods

        #region Private

        /// <summary>
        /// Initialize all properties.
        /// </summary>
        private void Initialize()
        {
            try
            {
                //Get GeoTransform и raster sizes.
                GeoTransform = Gdal.GetGeoTransform(InputFileInfo.FullName);
                (RasterXSize, RasterYSize) = Gdal.GetRasterSizes(InputFileInfo.FullName);
            }
            catch (Exception exception)
            {
                throw new
                    Exception($"Unable to get {nameof(GeoTransform)} value or {nameof(RasterXSize)}/{nameof(RasterYSize)}.",
                              exception);
            }

            double xMin = GeoTransform[0];
            double yMin = GeoTransform[3] - RasterYSize * GeoTransform[1];
            double xMax = GeoTransform[0] + RasterXSize * GeoTransform[1];
            double yMax = GeoTransform[3];

            //Create dictionary with tiles for each cropped zoom.
            for (int zoom = MinZ; zoom <= MaxZ; zoom++)
            {
                //Convert coordinates to tile numbers.
                (int tileMinX, int tileMinY, int tileMaxX, int tileMaxY) =
                    Tile.Tile.GetTileNumbersFromCoords(xMin, yMin, xMax, yMax, zoom);

                //Crop tiles extending world limits (+-180,+-90).
                tileMinX = Math.Max(0, tileMinX);
                tileMinY = Math.Max(0, tileMinY);
                tileMaxX = Math.Min(Convert.ToInt32(Math.Pow(2.0, zoom + 1)) - 1, tileMaxX);
                tileMaxY = Math.Min(Convert.ToInt32(Math.Pow(2.0, zoom)) - 1, tileMaxY);

                try
                {
                    MinMax.Add(zoom, new[] {tileMinX, tileMinY, tileMaxX, tileMaxY});
                }
                catch (Exception exception)
                {
                    throw new Exception($"Unable to add value to {nameof(MinMax)} dictionary.", exception);
                }
            }
        }

        /// <summary>
        /// Calculate size and positions to read/write.
        /// </summary>
        /// <param name="upperLeftX">Tile's upper left x coordinate.</param>
        /// <param name="upperLeftY">Tile's upper left y coordinate.</param>
        /// <param name="lowerRightX">Tile's lower right x coordinate.</param>
        /// <param name="lowerRightY">Tile's lower right y coordinate.</param>
        /// <returns>x/y positions and sizes to read; x/y positions and sizes to write tiles.</returns>
        private (int readXMin, int readYMin, int readXSize, int readYSize, int writeXMin, int writeYMin, int writeXSize,
            int writeYSize) GeoQuery(double upperLeftX,
                                     double upperLeftY,
                                     double lowerRightX,
                                     double lowerRightY)
        {
            //Geotiff coordinate borders.
            double tiffXMin = GeoTransform[0];
            double tiffYMin = GeoTransform[3] - RasterYSize * GeoTransform[1];
            double tiffXMax = GeoTransform[0] + RasterXSize * GeoTransform[1];
            double tiffYMax = GeoTransform[3];

            //Read from input geotiff in pixels.
            double readXMin = RasterXSize * (upperLeftX - tiffXMin) / (tiffXMax - tiffXMin);
            double readYMin = RasterYSize - RasterYSize * (upperLeftY - tiffYMin) / (tiffYMax - tiffYMin);
            double readXMax = RasterXSize * (lowerRightX - tiffXMin) / (tiffXMax - tiffXMin);
            double readYMax = RasterYSize - RasterYSize * (lowerRightY - tiffYMin) / (tiffYMax - tiffYMin);

            //If outside of tiff.
            readXMin = readXMin < 0.0 ? 0.0 :
                       readXMin > RasterXSize ? RasterXSize : readXMin;
            readYMin = readYMin < 0.0 ? 0.0 :
                       readYMin > RasterYSize ? RasterYSize : readYMin;
            readXMax = readXMax < 0.0 ? 0.0 :
                       readXMax > RasterXSize ? RasterXSize : readXMax;
            readYMax = readYMax < 0.0 ? 0.0 :
                       readYMax > RasterYSize ? RasterYSize : readYMax;

            //Output tile's borders in pixels.
            double tileXMin = readXMin.Equals(0.0) ? tiffXMin :
                              readXMin.Equals(RasterXSize) ? tiffXMax : upperLeftX;
            double tileYMin = readYMax.Equals(0.0) ? tiffYMax :
                              readYMax.Equals(RasterYSize) ? tiffYMin : lowerRightY;
            double tileXMax = readXMax.Equals(0.0) ? tiffXMin :
                              readXMax.Equals(RasterXSize) ? tiffXMax : lowerRightX;
            double tileYMax = readYMin.Equals(0.0) ? tiffYMax :
                              readYMin.Equals(RasterYSize) ? tiffYMin : upperLeftY;

            //Positions of dataset to write in tile.
            double writeXMin = Enums.Image.Image.TileSize -
                               Enums.Image.Image.TileSize * (lowerRightX - tileXMin) / (lowerRightX - upperLeftX);
            double writeYMin = Enums.Image.Image.TileSize * (upperLeftY - tileYMax) / (upperLeftY - lowerRightY);
            double writeXMax = Enums.Image.Image.TileSize -
                               Enums.Image.Image.TileSize * (lowerRightX - tileXMax) / (lowerRightX - upperLeftX);
            double writeYMax = Enums.Image.Image.TileSize * (upperLeftY - tileYMin) / (upperLeftY - lowerRightY);

            //Sizes to read and write.
            double readXSize = readXMax - readXMin;
            double readYSize = readYMax - readYMin;
            double writeXSize = writeXMax - writeXMin;
            double writeYSize = writeYMax - writeYMin;

            //Shifts.
            double readXShift = readXMin - (int) readXMin;
            readXSize += readXShift;
            double readYShift = readYMin - (int) readYMin;
            readYSize += readYShift;
            double writeXShift = writeXMin - (int) writeXMin;
            writeXSize += writeXShift;
            double writeYShift = writeYMin - (int) writeYMin;
            writeYSize += writeYShift;

            return ((int) readXMin, (int) readYMin,
                    (int) readXSize, (int) readYSize,
                    (int) writeXMin, (int) writeYMin,
                    (int) writeXSize, (int) writeYSize);
        }

        /// <summary>
        /// Writes one tile of current zoom.
        /// Crops zoom directly from input image.
        /// </summary>
        /// <param name="zoom">Zoom level.</param>
        /// <param name="currentX">Tile x.</param>
        /// <param name="currentY">Tile y.</param>
        /// <param name="inputImage">Input image.</param>
        private void WriteTile(int zoom, int currentX, int currentY, NetVips.Image inputImage)
        {
            const bool centreConvention = false;

            //Create directories for the tile. The overall structure looks like: outputDirectory/zoom/x/y.png.
            try
            {
                Directory.CreateDirectory(Path.Combine(OutputDirectoryInfo.FullName, $"{zoom}",
                                                       $"{currentX}"));
            }
            catch (Exception exception)
            {
                throw new Exception("Unable to create tile's directory.", exception);
            }

            (double xMin, double yMin, double xMax, double yMax) =
                Tile.Tile.TileBounds(currentX, currentY, zoom, false);

            //Get postitions and sizes for current tile.
            (int readXPos, int readYPos, int readXSize, int readYSize, int writeXPos, int writeYPos,
             int writeXSize, int writeYSize) = GeoQuery(xMin, yMax, xMax, yMin);

            string outputFilePath = Path.Combine(OutputDirectoryInfo.FullName, $"{zoom}",
                                                 $"{currentX}",
                                                 $"{currentY}{Enums.Extensions.Png}");

            // Scaling calculations
            double xFactor = (double) readXSize / writeXSize;
            double yFactor = (double) readYSize / writeYSize;

            // Calculate integral box shrink
            // We will get the best quality (but be the slowest) if we let reduce
            // do all the work. Leave it the final 200 - 300% to do as a compromise
            // for efficiency.
            int xShrink = Math.Max(1, (int) Math.Floor(1.0 / (xFactor * 2.0)));
            int yShrink = Math.Max(1, (int) Math.Floor(1.0 / (yFactor * 2.0)));

            // Calculate residual float affine transformation
            double xResidual = xShrink / xFactor;
            double yResidual = yShrink / yFactor;

            //Try open input image and crop tile
            NetVips.Image tile;
            try
            {
                tile = inputImage.Crop(readXPos, readYPos, readXSize, readYSize);
            }
            catch (Exception exception)
            {
                throw new Exception("Unable to create current tile.", exception);
            }

            // Fast, integral box-shrink
            if (yShrink > 1)
            {
                tile = tile.Shrinkv(yShrink);

                //Recalculate residual float.
                yResidual = (double) writeYSize / tile.Height;
            }
            if (xShrink > 1)
            {
                tile = tile.Shrinkh(xShrink);

                //Recalculate residual float.
                xResidual = (double) writeXSize / tile.Width;
            }

            // Perform kernel-based reduction
            if (yResidual < 1.0)
                tile = tile.Reducev(1.0 / yResidual, NetVips.Enums.Kernel.Lanczos3, centreConvention);
            if (xResidual < 1.0)
                tile = tile.Reduceh(1.0 / xResidual, NetVips.Enums.Kernel.Lanczos3, centreConvention);

            //Perform enlargement
            if (yResidual > 1.0 || xResidual > 1.0)
            {
                // Input displacement. For centre sampling, shift by 0.5 down and right.
                double id = centreConvention ? 0.5 : 0.0;

                // Floating point affine transformation
                using (Interpolate interpolate = Interpolate.NewFromName(Enums.Image.Interpolations.Bicubic))
                {
                    if (yResidual > 1.0 && xResidual > 1.0)
                        tile = tile.Affine(new[] {xResidual, 0.0, 0.0, yResidual}, interpolate,
                                           idx: id, idy: id,
                                           extend: NetVips.Enums.Extend.Copy);
                    else if (yResidual > 1.0)
                        tile = tile.Affine(new[] {1.0, 0.0, 0.0, yResidual}, interpolate, idx: id,
                                           idy: id,
                                           extend: NetVips.Enums.Extend.Copy);
                    else if (xResidual > 1.0)
                        tile = tile.Affine(new[] {xResidual, 0.0, 0.0, 1.0}, interpolate, idx: id,
                                           idy: id,
                                           extend: NetVips.Enums.Extend.Copy);
                }
            }

            // Add alpha channel if needed
            for (; tile.Bands < Enums.Image.Image.Bands;)
                tile = tile.Bandjoin(255);

            // Make a transparent image
            NetVips.Image outputImage;
            try
            {
                outputImage = NetVips.Image.Black(Enums.Image.Image.TileSize, Enums.Image.Image.TileSize)
                                     .NewFromImage(new[] {0, 0, 0, 0});
            }
            catch (Exception exception)
            {
                throw new Exception("Unable to write tile.", exception);
            }

            // Insert tile into output image
            outputImage = outputImage.Insert(tile, writeXPos, writeYPos);
            outputImage.Pngsave(outputFilePath);

            outputImage.Dispose();
            tile.Dispose();
        }

        /// <summary>
        /// Writes new tile by joining 4 lower ones.
        /// </summary>
        /// <param name="zoom">Current zoom level.</param>
        /// <param name="currentX">Tile's x number.</param>
        /// <param name="currentY">Tile's y number.</param>
        private void WriteTile(int zoom, int currentX, int currentY)
        {
            try
            {
                //Create directories for the tile. The overall structure looks like: outputDirectory/zoom/x/y.png.
                string tileDirectoryPath = Path.Combine(OutputDirectoryInfo.FullName, $"{zoom}", $"{currentX}");
                try
                {
                    Directory.CreateDirectory(tileDirectoryPath);
                }
                catch (Exception exception)
                {
                    throw new Exception($"Unable to create tile's directory here: {tileDirectoryPath}.",
                                        exception);
                }

                //Calculate upper tiles's positions.
                int newTileX1 = currentX * 2;
                int newTileY1 = currentY * 2;
                int newTileX2 = newTileX1 + 1;
                int newTileY2 = newTileY1;
                int newTileX3 = newTileX1;
                int newTileY3 = newTileY1 + 1;
                int newTileX4 = newTileX1 + 1;
                int newTileY4 = newTileY1 + 1;

                bool tilesExists = false;

                const int upperTileSize = Enums.Image.Image.TileSize / 2;

                NetVips.Image tile1;
                string tile1Path = Path.Combine(OutputDirectoryInfo.FullName, $"{zoom + 1}", $"{newTileX1}",
                                                $"{newTileY1}{Enums.Extensions.Png}");
                if (File.Exists(tile1Path))
                {
                    tile1 = NetVips.Image.Pngload(tile1Path);
                    tile1 = tile1.ThumbnailImage(upperTileSize, upperTileSize);
                    tilesExists = true;
                }
                else
                    tile1 = NetVips.Image.Black(upperTileSize, upperTileSize);

                NetVips.Image tile2;
                string tile2Path =
                    Path.Combine(OutputDirectoryInfo.FullName, $"{zoom + 1}", $"{newTileX2}",
                                 $"{newTileY2}{Enums.Extensions.Png}");
                if (File.Exists(tile2Path))
                {
                    tile2 = NetVips.Image.Pngload(tile2Path);
                    tile2 = tile2.ThumbnailImage(upperTileSize, upperTileSize);
                    tilesExists = true;
                }
                else
                    tile2 = NetVips.Image.Black(upperTileSize, upperTileSize);

                NetVips.Image tile3;
                string tile3Path =
                    Path.Combine(OutputDirectoryInfo.FullName, $"{zoom + 1}", $"{newTileX3}",
                                 $"{newTileY3}{Enums.Extensions.Png}");
                if (File.Exists(tile3Path))
                {
                    tile3 = NetVips.Image.Pngload(tile3Path);
                    tile3 = tile3.ThumbnailImage(upperTileSize, upperTileSize);
                    tilesExists = true;
                }
                else
                    tile3 = NetVips.Image.Black(upperTileSize, upperTileSize);

                NetVips.Image tile4;
                string tile4Path =
                    Path.Combine(OutputDirectoryInfo.FullName, $"{zoom + 1}", $"{newTileX4}",
                                 $"{newTileY4}{Enums.Extensions.Png}");
                if (File.Exists(tile4Path))
                {
                    tile4 = NetVips.Image.Pngload(tile4Path);
                    tile4 = tile4.ThumbnailImage(upperTileSize, upperTileSize);
                    tilesExists = true;
                }
                else
                    tile4 = NetVips.Image.Black(upperTileSize, upperTileSize);

                //We shouldn't create tiles, if they're not exist.
                if (!tilesExists) return;

                NetVips.Image[] images = { tile3, tile4, tile1, tile2 };
                for (int i = 0; i < images.Length; i++)
                {
                    int bands = images[i].Bands;
                    switch (bands)
                    {
                        case Enums.Image.Image.Bands:
                            continue;
                        case 1:
                            {
                                for (int j = bands; j < Enums.Image.Image.Bands; j++)
                                    images[i] = images[i].Bandjoin(0);
                                break;
                            }
                        default:
                            {
                                for (int j = bands; j < Enums.Image.Image.Bands; j++)
                                    images[i] = images[i].Bandjoin(255);
                                break;
                            }
                    }
                }

                NetVips.Image resultImage = NetVips.Image.Arrayjoin(images, 2);

                string outputTilePath = Path.Combine(OutputDirectoryInfo.FullName, $"{zoom}",
                                                     $"{currentX}",
                                                     $"{currentY}{Enums.Extensions.Png}");

                resultImage.Pngsave(outputTilePath);

                tile1.Dispose();
                tile2.Dispose();
                tile3.Dispose();
                tile4.Dispose();
                foreach (NetVips.Image image in images)
                    image.Dispose();
                resultImage.Dispose();
            }
            catch (Exception exception)
            {
                throw new Exception($"Unable to create upper level tile on zoom: {zoom}.", exception);
            }
        }

        /// <summary>
        /// Crops passed zoom to tiles.
        /// </summary>
        /// <param name="zoom">Current zoom to crop.</param>
        /// <param name="threadsCount">Threads count.</param>
        /// <returns></returns>
        private async ValueTask WriteZoom(int zoom, int threadsCount)
        {
            try
            {
                NetVips.Image inputImage = NetVips.Image.Tiffload(InputFileInfo.FullName, access: NetVips.Enums.Access.Random);
                using (SemaphoreSlim semaphoreSlim = new SemaphoreSlim(threadsCount))
                {
                    List<Task> tasks = new List<Task>();

                    //For each tile on given zoom calculate positions/sizes and save as file.
                    for (int currentY = MinMax[zoom][1]; currentY <= MinMax[zoom][3]; currentY++)
                    {
                        for (int currentX = MinMax[zoom][0]; currentX <= MinMax[zoom][2]; currentX++)
                        {
                            await semaphoreSlim.WaitAsync();

                            int x = currentX;
                            int y = currentY;

                            tasks.Add(Task.Run(() =>
                            {
                                try
                                {
                                    WriteTile(zoom, x, y, inputImage);
                                }
                                finally
                                {
                                    semaphoreSlim.Release();
                                }
                            }));
                        }
                    }

                    await Task.WhenAll(tasks);

                    //Dispose tasks.
                    foreach (Task task in tasks) task.Dispose();
                }

                inputImage.Dispose();
            }
            catch (Exception exception)
            {
                throw new Exception($"Unable to crop tiles of the following zoom:{zoom}.", exception);
            }
        }

        /// <summary>
        /// Make upper tiles from the lowest zoom.
        /// </summary>
        /// <param name="zoom">Zoom, for which we're cropping tiles atm.</param>
        /// <param name="threadsCount">Threads count.</param>
        /// <returns></returns>
        private async ValueTask MakeUpperTiles(int zoom, int threadsCount)
        {
            using (SemaphoreSlim semaphoreSlim = new SemaphoreSlim(threadsCount))
            {
                List<Task> tasks = new List<Task>();

                for (int currentY = MinMax[zoom][1]; currentY <= MinMax[zoom][3]; currentY++)
                {
                    for (int currentX = MinMax[zoom][0]; currentX <= MinMax[zoom][2]; currentX++)
                    {
                        await semaphoreSlim.WaitAsync();

                        int x = currentX;
                        int y = currentY;

                        tasks.Add(Task.Run(() =>
                        {
                            try
                            {
                                WriteTile(zoom, x, y);
                            }
                            finally
                            {
                                semaphoreSlim.Release();
                            }
                        }));
                    }
                }

                await Task.WhenAll(tasks);

                //Dispose tasks.
                foreach (Task task in tasks) task.Dispose();
            }
        }

        #endregion

        #region Public

        /// <summary>
        /// Create tiles. Crops input tiff only for lowest zoom and then join the higher ones from it.
        /// </summary>
        /// <param name="tempDirectoryInfo">Temp directory.</param>
        /// <param name="progress">Progress.</param>
        /// <param name="threadsCount">Threads count.</param>
        /// <returns></returns>
        public async ValueTask GenerateTilesByJoining(DirectoryInfo tempDirectoryInfo, IProgress<double> progress, int threadsCount)
        {
            //Check for errors.
            InputFileInfo = Helpers.CheckHelper.CheckInputFile(InputFileInfo, tempDirectoryInfo);
            Helpers.CheckHelper.CheckOutputDirectory(OutputDirectoryInfo);

            //Initialize properties.
            Initialize();

            //Crop lowest zoom level.
            await WriteZoom(MaxZ, threadsCount);
            double percentage = 1.0 / (MaxZ - MinZ + 1) * 100.0;
            progress.Report(percentage);

            //Crop upper tiles.
            for (int zoom = MaxZ - 1; zoom >= MinZ; zoom--)
            {
                await MakeUpperTiles(zoom, threadsCount);

                percentage = (double)(MaxZ - zoom + 1) / (MaxZ - MinZ + 1) * 100.0;
                progress.Report(percentage);
            }
        }

        /// <summary>
        /// Crops input tiff for each zoom.
        /// </summary>
        /// <param name="tempDirectoryInfo">Temp directory.</param>
        /// <param name="progress">Progress.</param>
        /// <param name="threadsCount">Threads count.</param>
        /// <returns></returns>
        public async ValueTask GenerateTilesByCropping(DirectoryInfo tempDirectoryInfo, IProgress<double> progress, int threadsCount)
        {
            //Check for errors.
            InputFileInfo = Helpers.CheckHelper.CheckInputFile(InputFileInfo, tempDirectoryInfo);
            Helpers.CheckHelper.CheckOutputDirectory(OutputDirectoryInfo);

            //Initialize properties.
            Initialize();

            //Crop tiles for each zoom.
            for (int zoom = MinZ; zoom <= MaxZ; zoom++)
            {
                await WriteZoom(zoom, threadsCount);

                double percentage = (double)(zoom - MinZ + 1) / (MaxZ - MinZ + 1) * 100.0;
                progress.Report(percentage);
            }
        }

        #endregion

        #endregion
    }
}
