using HelixToolkit.Wpf;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace Sharp3DRbxAvatar
{
    public class AvatarViewer
    {
        public static readonly string AvatarModelsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sharp3DRbxAvatar", "Models");
        private readonly AxisAngleRotation3D spinRotation = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
        private readonly AxisAngleRotation3D tiltRotation = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
        private readonly PerspectiveCamera camera = new PerspectiveCamera
        {
            Position = new Point3D(0, 30, 0),
            LookDirection = new Vector3D(0, -30, 0),
            UpDirection = new Vector3D(0, 0, 1),
            FieldOfView = 25
        };
        private readonly HelixViewport3D hVp3D = new HelixViewport3D
        {
            Background = Brushes.Transparent,

            LimitFPS = true,
            IsRotationEnabled = false,
            IsPanEnabled = false,
            RotateCursor = Cursors.Arrow,
            ZoomCursor = Cursors.Arrow,
            PanCursor = Cursors.Arrow,
            ShowCameraTarget = false,
            ShowViewCube = false,
        };

        private bool isDragging = false;
        private Point lastMousePosition;
        private double pausedAngle = 0;

        public AvatarViewer()
        {
            hVp3D.PreviewMouseLeftButtonDown += Viewport_MouseLeftButtonDown;
            hVp3D.PreviewMouseMove += Viewport_MouseMove;
            hVp3D.PreviewMouseLeftButtonUp += Viewport_MouseLeftButtonUp;
        }

        private void PauseSpinAnimation()
        {
            pausedAngle = spinRotation.Angle;
            spinRotation.BeginAnimation(AxisAngleRotation3D.AngleProperty, null);
            spinRotation.Angle = pausedAngle;
        }

        private void ResumeSpinAnimation()
        {
            var rotationAnimation = new DoubleAnimation
            {
                From = spinRotation.Angle,
                To = spinRotation.Angle + 360,
                Duration = TimeSpan.FromSeconds(8),
                RepeatBehavior = RepeatBehavior.Forever
            };
            spinRotation.BeginAnimation(AxisAngleRotation3D.AngleProperty, rotationAnimation);
        }

        private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            isDragging = true;
            lastMousePosition = e.GetPosition(hVp3D);
            PauseSpinAnimation();
            hVp3D.CaptureMouse();
            e.Handled = true;
        }

        private void Viewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isDragging) return;

            var currentPosition = e.GetPosition(hVp3D);
            var deltaX = currentPosition.X - lastMousePosition.X;
            var deltaY = currentPosition.Y - lastMousePosition.Y;

            spinRotation.Angle += deltaX * 0.5;
            var newTilt = tiltRotation.Angle - deltaY * 0.5;
            tiltRotation.Angle = Math.Max(-80, Math.Min(80, newTilt));

            lastMousePosition = currentPosition;
        }

        private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!isDragging) return;

            isDragging = false;
            hVp3D.ReleaseMouseCapture();
            ResumeSpinAnimation();
        }

        private static string GetAvatarVersionPath(long userId, int versionIndex = 0)
        {
            var userPath = Path.Combine(AvatarModelsPath, userId.ToString());

            if (!Directory.Exists(userPath))
                return null;

            var versionDirs = Directory.GetDirectories(userPath)
                .Select(dir => new DirectoryInfo(dir))
                .OrderByDescending(dir => dir.CreationTimeUtc)
                .ToList();

            if (versionIndex < 0 || versionIndex >= versionDirs.Count)
                return null;

            return versionDirs[versionIndex].FullName;
        }

        public static async Task<Model3D> LoadAvatarModel(long userId, int versionIndex = 0)
        {
            try
            {
                var versionPath = GetAvatarVersionPath(userId, versionIndex);
                if (versionPath == null)
                {
                    Console.WriteLine($"[LoadAvatarModel] Version path is NULL");
                    return null;
                }

                Console.WriteLine($"[LoadAvatarModel] Loading from: {versionPath}");

                var objPath = Path.Combine(versionPath, "model.obj");
                var mtlPath = Path.Combine(versionPath, "model.mtl");

                if (!File.Exists(objPath))
                {
                    Console.WriteLine($"[LoadAvatarModel] OBJ not found");
                    return null;
                }

                if (!File.Exists(mtlPath))
                {
                    Console.WriteLine($"[LoadAvatarModel] MTL not found");
                    return null;
                }

                Stream mtlStream = new MemoryStream(Encoding.UTF8.GetBytes(FixMtlTexturePaths(mtlPath, versionPath)));
                var importer = new ObjReader();
                Model3D model;

                using (var objStream = File.OpenRead(objPath))
                {
                    var streams = mtlStream != null ? new[] { mtlStream } : Array.Empty<Stream>();
                    try
                    {
                        model = importer.Read(objStream, streams);
                        Console.WriteLine($"[LoadAvatarModel] Model loaded!");
                    }
                    finally
                    {
                        foreach (var stream in streams)
                            stream?.Dispose();
                    }
                }

                if (model == null)
                {
                    Console.WriteLine($"[LoadAvatarModel] Model is NULL!");
                    return null;
                }

                return model;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LoadAvatarModel] EXCEPTION: {ex.Message}");
                Console.WriteLine($"[LoadAvatarModel] StackTrace: {ex.StackTrace}");
                return null;
            }
        }

        private static string FixMtlTexturePaths(string mtlPath, string basePath)
        {
            var lines = File.ReadAllLines(mtlPath);
            var result = new StringBuilder();

            var textureDirectives = new[] { "map_Ka", "map_Kd", "map_Ks", "map_Ns", "map_d", "map_bump", "bump", "disp" };

            foreach (var line in lines)
            {
                result.AppendLine(line);

                var trimmed = line.Trim();

                foreach (var directive in textureDirectives)
                {
                    if (trimmed.StartsWith(directive + " ", StringComparison.OrdinalIgnoreCase))
                    {
                        var textureFileName = trimmed.Substring(directive.Length).Trim();
                        var parts = textureFileName.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        textureFileName = parts[parts.Length - 1];

                        if (Path.IsPathRooted(textureFileName))
                            continue;

                        var absolutePath = Path.Combine(basePath, textureFileName);
                        if (File.Exists(absolutePath))                        
                            result.AppendLine($"{directive} {absolutePath}");                        

                        break;
                    }
                }
            }

            return result.ToString();
        }

        public async Task<bool> SetupViewport(long userId, int versionIndex = 0)
        {
            try
            {
                hVp3D.Children.Clear();

                var model = await LoadAvatarModel(userId, versionIndex);
                if (model == null)
                    return false;

                var bounds = model.Bounds;
                double pedestalRadius = 7;
                double pedestalHeight = 10;
                double floorY = bounds.Y - pedestalHeight;
                double floorHalfExtent = 25;

                var pedestalBuilder = new MeshBuilder(true, true);
                pedestalBuilder.AddCylinder(new Point3D(0, floorY, 0), new Point3D(0, bounds.Y, 0), pedestalRadius, 48, true, true);
                var pedestalMesh = pedestalBuilder.ToMesh();

                var pedestalMaterialGroup = new MaterialGroup();
                pedestalMaterialGroup.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(22, 22, 35))));
                pedestalMaterialGroup.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(80, 80, 120)), 40));
                var pedestalModel = new GeometryModel3D(pedestalMesh, pedestalMaterialGroup);

                // Floor
                var floorMesh = new MeshGeometry3D
                {
                    Positions = new Point3DCollection
                    {
                        new Point3D(-floorHalfExtent, floorY, - floorHalfExtent),
                        new Point3D(floorHalfExtent, floorY, - floorHalfExtent),
                        new Point3D(floorHalfExtent, floorY, floorHalfExtent),
                        new Point3D(-floorHalfExtent, floorY, floorHalfExtent)
                    },
                    TriangleIndices = new Int32Collection { 0, 2, 1, 0, 3, 2 },
                    Normals = new Vector3DCollection
                    {
                        new Vector3D(0, 1, 0), new Vector3D(0, 1, 0),
                        new Vector3D(0, 1, 0), new Vector3D(0, 1, 0)
                    },
                    TextureCoordinates = new PointCollection
                    {
                        new Point(0, 0), new Point(1, 0),
                        new Point(1, 1), new Point(0, 1)
                    }
                };

                var floorBrush = new RadialGradientBrush();
                floorBrush.GradientStops.Add(new GradientStop(Color.FromRgb(6, 6, 10), 0.0));
                floorBrush.GradientStops.Add(new GradientStop(Color.FromRgb(48, 48, 68), 0.08));
                floorBrush.GradientStops.Add(new GradientStop(Color.FromRgb(42, 42, 60), 0.25));
                floorBrush.GradientStops.Add(new GradientStop(Color.FromRgb(10, 10, 16), 0.45));
                floorBrush.GradientStops.Add(new GradientStop(Color.FromRgb(3, 3, 5), 0.65));
                floorBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0, 0, 0), 1.0));

                var floorMaterialGroup = new MaterialGroup();
                floorMaterialGroup.Children.Add(new DiffuseMaterial(Brushes.Black));
                floorMaterialGroup.Children.Add(new EmissiveMaterial(floorBrush));
                var floorModel = new GeometryModel3D(floorMesh, floorMaterialGroup);

                double spotY = bounds.Y + bounds.SizeY * 2;
                var spotLight = new SpotLight
                {
                    Color = Colors.White,
                    Position = new Point3D(0, spotY, 0),
                    Direction = new Vector3D(0, -1, 0),
                    InnerConeAngle = 25,
                    OuterConeAngle = 55,
                    Range = bounds.SizeY * 5,
                    ConstantAttenuation = 1.0,
                    LinearAttenuation = 0,
                    QuadraticAttenuation = 0
                };

                var ambientLight = new AmbientLight(Color.FromRgb(25, 25, 35));

                var sceneGroup = new Model3DGroup();
                sceneGroup.Children.Add(ambientLight);
                sceneGroup.Children.Add(spotLight);
                sceneGroup.Children.Add(model);
                sceneGroup.Children.Add(pedestalModel);
                sceneGroup.Children.Add(floorModel);

                var translateTransform = new TranslateTransform3D(0, -103, 0.1);
                var initialRotation = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 90);
                var initialRotateTransform = new RotateTransform3D(initialRotation);
                var spinRotateTransform = new RotateTransform3D(spinRotation);
                var tiltRotateTransform = new RotateTransform3D(tiltRotation);

                tiltRotation.Angle = 0;

                var transformGroup = new Transform3DGroup();
                transformGroup.Children.Add(translateTransform);
                transformGroup.Children.Add(initialRotateTransform);
                transformGroup.Children.Add(spinRotateTransform);
                transformGroup.Children.Add(tiltRotateTransform);

                sceneGroup.Transform = transformGroup;

                var rotationAnimation = new DoubleAnimation
                {
                    From = 0,
                    To = 360,
                    Duration = TimeSpan.FromSeconds(8),
                    RepeatBehavior = RepeatBehavior.Forever
                };
                spinRotation.BeginAnimation(AxisAngleRotation3D.AngleProperty, rotationAnimation);

                hVp3D.Children.Add(new ModelVisual3D { Content = sceneGroup });
                hVp3D.Camera = camera;

                camera.LookDirection = new Vector3D(0, -30, 0);
                camera.Position = new Point3D(0, 30, 0);

                var cameraAnimation = new Point3DAnimation
                {
                    From = new Point3D(0, 1200, 0),
                    To = new Point3D(0, 30, 0),
                    Duration = TimeSpan.FromSeconds(0.8),
                    FillBehavior = FillBehavior.Stop,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                camera.BeginAnimation(PerspectiveCamera.PositionProperty, cameraAnimation);                
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("AvatarViewer::SetupViewport" + $"Failed to setup viewport: {ex.Message}");
                return false;
            }
        }

        public static List<(string VersionHash, DateTime CreatedAt, int Index)> GetAvailableVersions(long userId)
        {
            var userPath = Path.Combine(AvatarModelsPath, $"user_{userId}");

            if (!Directory.Exists(userPath))
                return new List<(string, DateTime, int)>();

            return Directory.GetDirectories(userPath)
                .Select(dir => new DirectoryInfo(dir))
                .OrderByDescending(dir => dir.CreationTimeUtc)
                .Select((dir, index) => (dir.Name, dir.CreationTimeUtc, index))
                .ToList();
        }

        public HelixViewport3D GetViewport() => hVp3D;
    }


    public partial class MainWindow : Window
    {
        private static readonly HttpClient _downloadClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        private readonly AvatarViewer avatarViewer = new AvatarViewer();
        private List<string> userFolders = new List<string>();
        private int currentUserIndex = -1;
        private int currentVersionIndex = 0;
        private int currentVersionCount = 0;

        private FileSystemWatcher folderWatcher;
        private readonly DispatcherTimer debounceTimer;

        public MainWindow()
        {
            InitializeComponent();
            DisplayGrid.Children.Add(avatarViewer.GetViewport());

            debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            debounceTimer.Tick += async (s, e) =>
            {
                debounceTimer.Stop();
                await OnFolderStructureChanged();
            };

            Loaded += async (s, e) =>
            {
                MessageBox.Show(
                    "This application uses a proprietary server owned by M4A1 (getevx.xyz) to download Roblox avatar 3D models.\n\n" +
                    "If you intend to deploy or redistribute this application, you are responsible for implementing your own model downloading solution. " +
                    "You are NOT authorized to use M4A1's server unless explicitly permitted by M4A1.\n\n" +
                    "Unauthorized use of the server is strictly prohibited.",
                    "Third-Party Server Notice",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                await InitializeAndLoad();
            };
            Closed += (s, e) => StopWatcher();
        }

        private async Task InitializeAndLoad()
        {
            RefreshUserList();

            if (userFolders.Count > 0)
            {
                currentUserIndex = 0;
                currentVersionIndex = 0;
                await LoadCurrentAvatar();
            }
            else
            {
                TxtUserName.Text = "None";
                TxtVersionInfo.Text = "—";
            }

            UpdateButtonStates();
            StartWatcher();
        }

        private void StartWatcher()
        {
            StopWatcher();

            var basePath = AvatarViewer.AvatarModelsPath;
            if (!Directory.Exists(basePath))
                Directory.CreateDirectory(basePath);

            folderWatcher = new FileSystemWatcher(basePath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.DirectoryName
            };

            folderWatcher.Created += OnFolderChanged;
            folderWatcher.Deleted += OnFolderChanged;
            folderWatcher.Renamed += OnFolderRenamed;
            folderWatcher.EnableRaisingEvents = true;
        }

        private void StopWatcher()
        {
            if (folderWatcher != null)
            {
                folderWatcher.EnableRaisingEvents = false;
                folderWatcher.Created -= OnFolderChanged;
                folderWatcher.Deleted -= OnFolderChanged;
                folderWatcher.Renamed -= OnFolderRenamed;
                folderWatcher.Dispose();
                folderWatcher = null;
            }
        }

        private void OnFolderChanged(object sender, FileSystemEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                debounceTimer.Stop();
                debounceTimer.Start();
            }));
        }

        private void OnFolderRenamed(object sender, RenamedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                debounceTimer.Stop();
                debounceTimer.Start();
            }));
        }

        private async Task OnFolderStructureChanged()
        {
            var previousUser = (currentUserIndex >= 0 && currentUserIndex < userFolders.Count) ? userFolders[currentUserIndex] : null;
            RefreshUserList();

            if (userFolders.Count == 0)
            {
                currentUserIndex = -1;
                currentVersionIndex = 0;
                currentVersionCount = 0;
                TxtUserName.Text = "None";
                TxtUserLabel.Text = "USER";
                TxtVersionInfo.Text = "—";
                TxtVersionLabel.Text = "AVATAR VERSION";
                UpdateButtonStates();
                return;
            }

            if (previousUser != null && userFolders.Contains(previousUser))
            {
                currentUserIndex = userFolders.IndexOf(previousUser);
                var newVersionCount = GetVersionCount(previousUser);
                if (currentVersionIndex >= newVersionCount)
                    currentVersionIndex = Math.Max(0, newVersionCount - 1);
            }
            else
            {
                currentUserIndex = Math.Min(currentUserIndex, userFolders.Count - 1);
                currentUserIndex = Math.Max(0, currentUserIndex);
                currentVersionIndex = 0;
            }

            await LoadCurrentAvatar();
        }

        private void RefreshUserList()
        {
            var basePath = AvatarViewer.AvatarModelsPath;
            userFolders.Clear();

            if (Directory.Exists(basePath))
            {
                userFolders = Directory.GetDirectories(basePath)
                    .Select(d => new DirectoryInfo(d))
                    .OrderBy(d => d.Name)
                    .Select(d => d.Name)
                    .ToList();
            }
        }

        private int GetVersionCount(string userFolder)
        {
            var userPath = Path.Combine(AvatarViewer.AvatarModelsPath, userFolder);
            if (!Directory.Exists(userPath))
                return 0;
            return Directory.GetDirectories(userPath).Length;
        }

        private async Task LoadCurrentAvatar()
        {
            if (currentUserIndex < 0 || currentUserIndex >= userFolders.Count)
                return;

            var userFolder = userFolders[currentUserIndex];
            currentVersionCount = GetVersionCount(userFolder);

            TxtUserName.Text = userFolder;
            TxtUserLabel.Text = $"USER {currentUserIndex + 1}/{userFolders.Count}";

            if (currentVersionCount > 0)
            {
                TxtVersionInfo.Text = $"{currentVersionIndex + 1} / {currentVersionCount}";
            }
            else
            {
                TxtVersionInfo.Text = "—";
            }

            if (long.TryParse(userFolder, out var userId))
            {
                var success = await avatarViewer.SetupViewport(userId, currentVersionIndex);
                if (!success)
                    TxtVersionInfo.Text = "Error";
            }

            UpdateButtonStates();
        }

        private void UpdateButtonStates()
        {
            BtnPrevUser.IsEnabled = currentUserIndex > 0;
            BtnNextUser.IsEnabled = currentUserIndex < userFolders.Count - 1;
            BtnPrevVersion.IsEnabled = currentVersionIndex > 0;
            BtnNextVersion.IsEnabled = currentVersionIndex < currentVersionCount - 1;
        }

        private async void BtnPrevUser_Click(object sender, RoutedEventArgs e)
        {
            if (currentUserIndex > 0)
            {
                currentUserIndex--;
                currentVersionIndex = 0;
                await LoadCurrentAvatar();
            }
        }

        private async void BtnNextUser_Click(object sender, RoutedEventArgs e)
        {
            if (currentUserIndex < userFolders.Count - 1)
            {
                currentUserIndex++;
                currentVersionIndex = 0;
                await LoadCurrentAvatar();
            }
        }

        private async void BtnPrevVersion_Click(object sender, RoutedEventArgs e)
        {
            if (currentVersionIndex > 0)
            {
                currentVersionIndex--;
                await LoadCurrentAvatar();
            }
        }

        private async void BtnNextVersion_Click(object sender, RoutedEventArgs e)
        {
            if (currentVersionIndex < currentVersionCount - 1)
            {
                currentVersionIndex++;
                await LoadCurrentAvatar();
            }
        }

        private async void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            var input = TxtDownloadId.Text?.Trim();
            if (!long.TryParse(input, out var userId) || userId <= 0)
            {
                TxtDownloadStatus.Text = "Invalid ID. Numbers only.";
                return;
            }

            BtnDownload.IsEnabled = false;
            TxtDownloadStatus.Text = "Downloading...";

            try
            {
                var response = await _downloadClient.GetAsync($"https://api.getevx.xyz/client/avatar/{userId}");

                if (!response.IsSuccessStatusCode)
                {
                    TxtDownloadStatus.Text = $"Server error: {(int)response.StatusCode}";
                    return;
                }

                var zipBytes = await response.Content.ReadAsByteArrayAsync();
                var destPath = Path.Combine(AvatarViewer.AvatarModelsPath, userId.ToString());
                Directory.CreateDirectory(destPath);

                using (var zipStream = new MemoryStream(zipBytes))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name))
                            continue;

                        var entryPath = Path.Combine(destPath, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(entryPath));

                        using (var entryStream = entry.Open())
                        using (var fileStream = File.Create(entryPath))
                            entryStream.CopyTo(fileStream);
                    }
                }

                TxtDownloadStatus.Text = $"Model {userId} downloaded successfully!";
                TxtDownloadId.Clear();
            }
            catch (Exception ex)
            {
                TxtDownloadStatus.Text = $"Error: {ex.Message}";
            }
            finally
            {
                BtnDownload.IsEnabled = true;
            }
        }

    }
}
