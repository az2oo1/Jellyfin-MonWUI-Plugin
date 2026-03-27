using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.JMSFusion.Core;

namespace Jellyfin.Plugin.JMSFusion
{
    public class JMSFusionPlugin : BasePlugin<JMSFusionConfiguration>, IHasWebPages
    {
        private static readonly Guid ExternalTransformationId = Guid.Parse("4f1983e0-55d8-45c7-a1ff-39a91d7ce412");

        public override string Name => "JMSFusion";
        public override Guid Id => Guid.Parse("c0b4a5e0-2f6a-4e70-9c5f-1e7c2d0b7f12");
        public override string Description => "Inject custom JS into Jellyfin UI using File Transformation plugin integration.";

        private readonly ILogger<JMSFusionPlugin> _logger;
        public static JMSFusionPlugin Instance { get; private set; } = null!;

        public JMSFusionPlugin(IApplicationPaths paths, IXmlSerializer xmlSerializer, ILoggerFactory loggerFactory)
            : base(paths, xmlSerializer)
        {
            _logger = loggerFactory.CreateLogger<JMSFusionPlugin>();
            Instance = this;

            ConfigurationChanged += (_, __) =>
            {
                _logger.LogInformation("[JMSFusion] Configuration changed.");

                if (Configuration.UseExternalFileTransformation)
                {
                    TryRegisterExternalFileTransformation();
                }

                if (Configuration.EnableLegacyIndexInjection)
                {
                    TryPatchIndexHtml();
                }
            };

            if (Configuration.UseExternalFileTransformation)
            {
                TryRegisterExternalFileTransformation();
            }

            if (Configuration.EnableLegacyIndexInjection)
            {
                TryPatchIndexHtml();

                _ = Task.Run(async () =>
                {
                    for (var i = 0; i < 3; i++)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3 * (i + 1)));
                        TryPatchIndexHtml();
                    }
                });
            }

            try
            {
                if (Configuration.EnableLegacyIndexInjection && Configuration.EnableTransformEngine)
                {
                    ResponseTransformation.Register(@".*index\.html(\.gz|\.br)?$",
                        req =>
                        {
                            var html = req.Contents ?? string.Empty;

                            _logger.LogInformation(
                                "[JMSFusion][DIAG] Transform hit for {Path} (len={Len})",
                                req.FilePath, html.Length
                            );

                            if (html.IndexOf("<!-- SL-INJECT BEGIN -->", StringComparison.OrdinalIgnoreCase) >= 0)
                                return html;

                            var snippet = BuildScriptsHtml();
                            var headEndIndex = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
                            if (headEndIndex >= 0)
                            {
                                return html.Insert(headEndIndex, "\n" + snippet + "\n");
                            }

                            return html + "\n" + snippet + "\n";
                        });

                    _logger.LogInformation("[JMSFusion] Registered in-memory transformation rule for .*index.html(+gz/br)");
                }
                else
                {
                    _logger.LogInformation("[JMSFusion] Legacy transform engine disabled by configuration");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[JMSFusion] Failed to register in-memory transformation; middleware/patch fallback will be used.");
            }
        }

        private bool TryRegisterExternalFileTransformation()
        {
            try
            {
                var fileTransformationAssembly = AssemblyLoadContext.All
                    .SelectMany(x => x.Assemblies)
                    .FirstOrDefault(x =>
                        (x.FullName?.Contains(".FileTransformation", StringComparison.OrdinalIgnoreCase) ?? false) ||
                        string.Equals(x.GetName().Name, "Jellyfin.Plugin.FileTransformation", StringComparison.OrdinalIgnoreCase));

                if (fileTransformationAssembly == null)
                {
                    _logger.LogWarning("[JMSFusion] File Transformation plugin assembly not loaded. Install/enable it to avoid index conflicts with other UI plugins.");
                    return false;
                }

                var pluginInterfaceType = fileTransformationAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
                var registerMethod = pluginInterfaceType?.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static);
                if (registerMethod == null)
                {
                    _logger.LogWarning("[JMSFusion] File Transformation PluginInterface.RegisterTransformation not found.");
                    return false;
                }

                var payload = BuildExternalTransformationPayload();
                registerMethod.Invoke(null, new[] { payload });

                _logger.LogInformation("[JMSFusion] Registered File Transformation hook for index.html ({Id}).", ExternalTransformationId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[JMSFusion] Failed to register with File Transformation plugin.");
                return false;
            }
        }

        private object BuildExternalTransformationPayload()
        {
            var objType = Type.GetType("Newtonsoft.Json.Linq.JObject, Newtonsoft.Json")
                          ?? throw new InvalidOperationException("Newtonsoft JObject type not found.");
            var tokenType = Type.GetType("Newtonsoft.Json.Linq.JToken, Newtonsoft.Json")
                            ?? throw new InvalidOperationException("Newtonsoft JToken type not found.");
            var valueType = Type.GetType("Newtonsoft.Json.Linq.JValue, Newtonsoft.Json")
                            ?? throw new InvalidOperationException("Newtonsoft JValue type not found.");

            var add = objType.GetMethod("Add", new[] { typeof(string), tokenType })
                      ?? throw new InvalidOperationException("JObject.Add method not found.");

            var obj = Activator.CreateInstance(objType)
                      ?? throw new InvalidOperationException("Could not create JObject instance.");

            void Add(string key, object? value)
            {
                var token = Activator.CreateInstance(valueType, value)
                            ?? throw new InvalidOperationException("Could not create JValue token.");
                add.Invoke(obj, new[] { key, token });
            }

            Add("id", ExternalTransformationId);
            Add("fileNamePattern", @"(^|/)index\.html$");
            Add("callbackAssembly", GetType().Assembly.FullName ?? typeof(JMSFusionPlugin).Assembly.FullName ?? string.Empty);
            Add("callbackClass", typeof(JMSFusionPlugin).FullName ?? "Jellyfin.Plugin.JMSFusion.JMSFusionPlugin");
            Add("callbackMethod", nameof(ApplyExternalTransformation));

            return obj;
        }

        public sealed class ExternalTransformationRequest
        {
            public string Contents { get; set; } = string.Empty;
        }

        public static string ApplyExternalTransformation(ExternalTransformationRequest req)
        {
            var html = req?.Contents ?? string.Empty;
            if (html.IndexOf("<!-- SL-INJECT BEGIN -->", StringComparison.OrdinalIgnoreCase) >= 0)
                return html;

            var plugin = Instance;
            if (plugin == null)
                return html;

            var snippet = plugin.BuildScriptsHtml();
            var headEnd = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            if (headEnd >= 0)
            {
                return html.Insert(headEnd, "\n" + snippet + "\n");
            }

            return html + "\n" + snippet + "\n";
        }

        private string? DetectWebRoot()
        {
            try
            {
                var webPath = ApplicationPaths.WebPath;
                if (!string.IsNullOrWhiteSpace(webPath) &&
                    Directory.Exists(webPath) &&
                    File.Exists(Path.Combine(webPath, "index.html")))
                {
                    _logger.LogInformation("[JMSFusion] Using ApplicationPaths.WebPath as web root: {WebRoot}", webPath);
                    return webPath;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[JMSFusion] Failed probing ApplicationPaths.WebPath");
            }

            var candidates = new[]
            {
                "/usr/share/jellyfin/web",
                "/var/lib/jellyfin/web",
                "/opt/jellyfin/web",
                "/jellyfin/web",
                Path.Combine(Environment.CurrentDirectory, "web"),
                Path.Combine(AppContext.BaseDirectory, "web")
            };

            foreach (var p in candidates)
            {
                try
                {
                    _logger.LogInformation("[JMSFusion] Checking web root candidate: {Candidate}", p);

                    if (Directory.Exists(p) && File.Exists(Path.Combine(p, "index.html")))
                    {
                        _logger.LogInformation("[JMSFusion] Found web root: {WebRoot}", p);
                        return p;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[JMSFusion] Error checking candidate: {Candidate}", p);
                }
            }

            _logger.LogWarning("[JMSFusion] Web root not found in any candidate location");
            return null;
        }

        public void TryPatchIndexHtml()
        {
            try
            {
                var root = DetectWebRoot();
                if (string.IsNullOrWhiteSpace(root))
                {
                    _logger.LogWarning("[JMSFusion] Web root not found; skipping patch.");
                    return;
                }

                var ok = IndexPatcher.EnsurePatched(_logger, root);
                _logger.LogInformation("[JMSFusion] Patch result: {ok}", ok);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[JMSFusion] TryPatchIndexHtml failed");
            }
        }

        public string BuildScriptsHtml(string? pathBase = null)
        {
            var ver = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var sb = new StringBuilder();
            sb.AppendLine("<!-- SL-INJECT BEGIN -->");
            sb.AppendLine($@"<script type=""module"" src=""../slider/main.js?v={ver}""></script>");
            sb.AppendLine("<!-- SL-INJECT END -->");
            return sb.ToString();
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            var ns = typeof(JMSFusionPlugin).Namespace;
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "JMSFusionConfigPage",
                    EmbeddedResourcePath = $"{ns}.Web.configuration.html",
                    EnableInMainMenu = true,
                    MenuSection = "server",
                    MenuIcon = "developer_mode"
                }
            };
        }
    }
}
