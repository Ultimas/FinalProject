using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using Cv = OpenCvSharp;

namespace SdkExample
{
    class Example
    {
        public static int Main(string[] args)
        {
            // Parse input arguments
            string videoPath, facePath, studyPath, output;
            int parseResult = ParseArgs(args, out videoPath, out facePath, out studyPath, out output);
            if (parseResult != 2)
                return parseResult;

            // Create a Factory object
            var factory = new Dfx.Sdk.Factory();
            Console.WriteLine($"Created DFX Factory: {factory.Version}");

            // Initialize a study
            if (!factory.InitializeStudyFromFile(studyPath))
            {
                Console.WriteLine($"DFX study initialization failed: {factory.LastErrorMessage}");
                return 1;
            }
            Console.WriteLine($"Created study from {studyPath}");

            // Create a collector
            var collector = factory.CreateCollector();
            if (collector.CurrentState == Dfx.Sdk.Collector.State.ERROR)
            {
                Console.WriteLine($"Collector creation failed: {collector.LastErrorMessage}");
                Console.ReadKey();
                return 1;
            }
            Console.WriteLine("Created collector");

            // Load the face tracking data
            var jsonFaces = LoadJsonFaces(facePath);

            // Load video file (or stream of images)
            var videocap = Cv.VideoCapture.FromFile(videoPath);
            var videoFileName = Path.GetFileName(videoPath);

            // Set target FPS and chunk duration
            double targetFps = videocap.Get(Cv.CaptureProperty.Fps);
            double videoFrameCount = videocap.Get(Cv.CaptureProperty.FrameCount);
            const int chunkDuration_s = 5;
            const int KLUDGE = 1;
            double chunkFrameCount = Math.Ceiling(chunkDuration_s * targetFps + KLUDGE);
            ulong numberChunks = (ulong)Math.Ceiling(videoFrameCount / chunkFrameCount); // Ask more chunks then needed
            double durationOfOneFrame_ns = 1000_000_000.0 / targetFps;

            collector.TargetFps = (float)targetFps;
            collector.ChunkDuration = chunkDuration_s;
            collector.NumberChunks = numberChunks;

            Console.WriteLine($"    mode: {factory.Mode}");
            Console.WriteLine($"    number chunks: {collector.NumberChunks}");
            Console.WriteLine($"    chunk duration: {collector.ChunkDuration}");
            foreach (var constraint in collector.GetEnabledConstraints())
                Console.WriteLine($"    enabled constraint: {constraint}");

            // Start collection
            collector.StartCollection();

            // Start reading frames and adding to collector
            uint frameNumber = 0;
            bool success = false;
            using (var window = new Cv.Window("capture"))
            {
                Cv.Mat image = new Cv.Mat();
                while (true)
                {
                    bool ret = videocap.Read(image);
                    if (!ret || image.Empty())
                    {
                        // Video ended, so grab what should be the last, possibly truncated chunk
                        var chunkData = collector.ChunkData;
                        if (chunkData != null)
                        {
                            var chunkPayload = chunkData.Payload;
                            //if (output != null)
                            //    savePayload(chunkPayload, output);
                            Console.WriteLine($"Got chunk with {chunkPayload}");
                        }
                        else
                        {
                            Console.WriteLine("Got empty chunk");
                        }
                        success = true;
                        break;
                    }

                    // Create a Dfx VideoFrame
                    using (Dfx.Sdk.VideoFrame videoFrame = new Dfx.Sdk.VideoFrame((ushort)image.Rows,
                                                                                  (ushort)image.Cols,
                                                                                  Dfx.Sdk.PixelType.TYPE_8UC3,
                                                                                  image.Channels() * image.Cols,
                                                                                  image.Data,
                                                                                  Dfx.Sdk.ChannelOrder.BGR,
                                                                                  (ulong)(frameNumber * durationOfOneFrame_ns),
                                                                                  frameNumber))
                    {
                        frameNumber++;

                        // Create a Dfx Frame from the VideoFrame
                        var frame = collector.CreateFrame(videoFrame);

                        // Add the Dfx Face to the Dfx Frame
                        var jsonFace = jsonFaces[frameNumber.ToString()];
                        var face = new Dfx.Sdk.Face((string)jsonFace["id"]);
                        face.PoseValid = (bool)jsonFace["poseValid"];
                        face.Detected = (bool)jsonFace["detected"];
                        face.SetRect((ushort)jsonFace["rect.x"], (ushort)jsonFace["rect.y"], (ushort)jsonFace["rect.w"], (ushort)jsonFace["rect.h"]);
                        foreach (JProperty entry in jsonFace["points"])
                        {
                            face.AddPosePoint(entry.Name, new Dfx.Sdk.PosePoint((float)entry.Value["x"],
                                                                                (float)entry.Value["y"],
                                                                                0,
                                                                                (bool)entry.Value["valid"],
                                                                                (bool)entry.Value["estimated"],
                                                                                (float)entry.Value["quality"]));
                        }
                        frame.AddFace(face);

                        // Add a marker to the 1000th dfx_frame
                        if (frameNumber == 1000)
                            frame.AddMarker("This is the 1000th frame");

                        // Do the extraction
                        collector.DefineRegions(frame);
                        var result = collector.ExtractChannels(frame);

                        // Grab a chunk and check if we are finished
                        if (result == Dfx.Sdk.Collector.State.CHUNKREADY || result == Dfx.Sdk.Collector.State.COMPLETED)
                        {
                            var chunkData = collector.ChunkData;
                            if (chunkData != null)
                            {
                                var chunkPayload = chunkData.Payload;
                                //if (output != null)
                                //    savePayload(chunkPayload, output);
                                Console.WriteLine($"Got chunk with {chunkPayload}");
                            }
                            else
                            {
                                Console.WriteLine("Got empty chunk");
                            }
                            if (result == Dfx.Sdk.Collector.State.COMPLETED)
                            {
                                Console.WriteLine($"{nameof(Dfx.Sdk.Collector.State.COMPLETED)} at frame {frameNumber}");
                                success = true;
                                break;
                            }
                        }

                        // Render
                        if (true)
                        {
                            foreach (var faceID in frame.FaceIdentifiers)
                                foreach (var regionID in frame.GetRegionNames(faceID))
                                    if (frame.GetRegionIntProperty(faceID, regionID, "draw") != 0)
                                    {
                                        var dfxpolygon = frame.GetRegionPolygon(faceID, regionID);
                                        var cvpolygon = new List<Cv.Point>();
                                        foreach (var point in dfxpolygon)
                                        {
                                            cvpolygon.Add(new Cv.Point(point.X, point.Y));
                                        }
                                        var cvpolygons = new List<List<Cv.Point>>();
                                        cvpolygons.Add(cvpolygon);
                                        Cv.Cv2.Polylines(image, cvpolygons, isClosed: true, color: Cv.Scalar.Cyan, thickness: 1, lineType: Cv.LineTypes.AntiAlias);
                                    }

                            string msg = $"Extracting from {videoFileName} - frame {frameNumber} of {videoFrameCount}";
                            Cv.Cv2.PutText(image, msg, org: new Cv.Point(10, 30), fontFace: Cv.HersheyFonts.HersheyPlain, fontScale: 1, color: Cv.Scalar.Black, thickness: 1, lineType: Cv.LineTypes.AntiAlias);

                            window.ShowImage(image);
                            if (Cv.Cv2.WaitKey(1) == 'q')
                            {
                                success = false;
                                break;
                            }
                        }
                    }
                }
            }

            if (success)
                Console.WriteLine("Collection finished completely. Press any key to exit...");
            else
                Console.WriteLine("Collection interrupted or failed. Press any key to exit...");

            // When everything done, release the capture
            videocap.Release();

            Console.ReadKey();

            return 0;
        }

        private static int ParseArgs(string[] args, out string videoPath, out string facePath, out string studyPath, out string output)
        {
            videoPath = facePath = studyPath = output = null;

            var posArgs = new List<string>();
            for (int i = 0; i < args.Length; ++i)
            {
                string arg = args[i].ToLowerInvariant();
                if (arg == "-v" || arg == "--version")
                {
                    Console.WriteLine($"{System.AppDomain.CurrentDomain.FriendlyName} - {Dfx.Sdk.About.Version}");

                    return 0;
                }
                if (arg == "-h" || arg == "--help")
                {
                    PrintUsage();

                    return 0;
                }
                if (arg == "-o" || arg == "--output")
                {
                    if (i < args.Length - 1)
                        output = args[i + 1];
                    continue;
                }

                posArgs.Add(arg);
            }

            if (posArgs.Count != 3)
            {
                Console.WriteLine($"{System.AppDomain.CurrentDomain.FriendlyName} - Error");
                PrintUsage();

                return 1;
            }

            videoPath = posArgs[0];
            facePath = posArgs[1];
            studyPath = posArgs[2];

            return 2;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("DFX SDK C# example program");
            Console.WriteLine($"usage: {System.AppDomain.CurrentDomain.FriendlyName} [-h] [-v] [-o OUTPUT] videoPath facePath studyPath");
            Console.WriteLine("positional arguments:");
            Console.WriteLine("  videoPath                   Path of video file to process");
            Console.WriteLine("  facePath                    Path of face tracking data");
            Console.WriteLine("  studyPath                   Path of study file");
            Console.WriteLine("optional arguments:");
            Console.WriteLine("  -h, --help                  show this help message and exit");
            Console.WriteLine("  -v, --version               show program's version number and exit");
            Console.WriteLine("  -o OUTPUT, --output OUTPUT  folder to save chunks");
        }

        private static JObject LoadJsonFaces(string jsonFacesPath)
        {
            using (StreamReader r = new StreamReader(jsonFacesPath))
            {
                string json = r.ReadToEnd();
                return (JObject)JObject.Parse(json)["frames"];
            }
        }


    }
}
