﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using EventStore.Common.Utils;
using EventStore.Core.Services.Transport.Http;
using EventStore.Transport.Http;
using EventStore.Transport.Http.Codecs;
using EventStore.Transport.Http.EntityManagement;
using Microsoft.Extensions.Logging;

namespace EventStore.Core.Util
{
    public class MiniWeb
    {
        private readonly string _localWebRootPath;
        private readonly string _fileSystemRoot;
        private static readonly ILogger Logger = TraceLogger.GetLogger<MiniWeb>();

        public MiniWeb(string localWebRootPath) : this(localWebRootPath, GetWebRootFileSystemDirectory())
        {
        }

        public MiniWeb(string localWebRootPath, string fileSystemRoot)
        {
            if (Logger.IsInformationLevelEnabled()) Logger.Starting_MiniWeb_for(localWebRootPath, fileSystemRoot);
            _localWebRootPath = localWebRootPath;
            _fileSystemRoot = fileSystemRoot;
        }

        public void RegisterControllerActions(IHttpService service)
        {
            var pattern = _localWebRootPath + "/{*remaining_path}";
#if DEBUG
            if (Logger.IsTraceLevelEnabled()) Logger.BindingMiniWeb(pattern);
#endif
            service.RegisterAction(new ControllerAction(pattern, HttpMethod.Get, Codec.NoCodecs, new ICodec[] { Codec.ManualEncoding }, AuthorizationLevel.None), OnStaticContent);
        }

        private void OnStaticContent(HttpEntityManager http, UriTemplateMatch match)
        {
            var contentLocalPath = match.BoundVariables["remaining_path"];
            ReplyWithContent(http, contentLocalPath);
        }

        private void ReplyWithContent(HttpEntityManager http, string contentLocalPath)
        {
            //NOTE: this is fix for Mono incompatibility in UriTemplate behavior for /a/b{*C}
            if (("/" + contentLocalPath).StartsWith(_localWebRootPath, StringComparison.Ordinal))
            {
                contentLocalPath = contentLocalPath.Substring(_localWebRootPath.Length);
            }

#if DEBUG
            //if (Logger.IsTraceLevelEnabled()) Logger.RequestedFromMiniWeb(contentLocalPath);
#endif
            try
            {
                var extensionToContentType = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { ".png",  "image/png"} ,
                    { ".svg",  "image/svg+xml"} ,
                    { ".woff", "application/x-font-woff"} ,
                    { ".woff2", "application/x-font-woff"} ,
                    { ".ttf", "application/font-sfnt"} ,
                    { ".jpg",  "image/jpeg"} ,
                    { ".jpeg", "image/jpeg"} ,
                    { ".css",  "text/css"} ,
                    { ".htm",  "text/html"} ,
                    { ".html", "text/html"} ,
                    { ".js",   "application/javascript"} ,
                    { ".json",   "application/json"} ,
                    { ".ico",  "image/vnd.microsoft.icon"}
                };

                var extension = Path.GetExtension(contentLocalPath);
                var fullPath = Path.Combine(_fileSystemRoot, contentLocalPath);

                if (string.IsNullOrEmpty(extension)
                || !extensionToContentType.TryGetValue(extension.ToLowerInvariant(), out string contentType)
                || !File.Exists(fullPath))
                {
                    if (Logger.IsInformationLevelEnabled()) { Logger.Replying_404_for(contentLocalPath, fullPath); }
                    http.ReplyTextContent(
                        "Not Found", 404, "Not Found", "text/plain", null,
                        ex => { if (Logger.IsInformationLevelEnabled()) Logger.Error_while_replying_from_MiniWeb(ex); });
                }
                else
                {
                    var config = GetWebPageConfig(contentType);
                    var content = File.ReadAllBytes(fullPath);

                    http.Reply(content,
                                       config.Code,
                                       config.Description,
                                       config.ContentType,
                                       config.Encoding,
                                       config.Headers,
                                       ex => { if (Logger.IsInformationLevelEnabled()) Logger.Error_while_replying_from_MiniWeb(ex); });
                }
            }
            catch (Exception ex)
            {
                http.ReplyTextContent(ex.ToString(), 500, "Internal Server Error", "text/plain", null, exc => Logger.LogError(exc.ToString()));
            }
        }

        private static ResponseConfiguration GetWebPageConfig(string contentType)
        {
            var encoding = contentType.StartsWith("image", StringComparison.Ordinal) ? null : Helper.UTF8NoBom;
            int? cacheSeconds =
#if RELEASE || CACHE_WEB_CONTENT
                60*60; // 1 hour
#else
                null; // no caching
#endif
            return Configure.Ok(contentType, encoding, null, cacheSeconds, isCachePublic: true);
        }

        public static string GetWebRootFileSystemDirectory(string debugPath = null)
        {
            string fileSystemWebRoot;
            try
            {
                if (!string.IsNullOrEmpty(debugPath))
                {
                    var sf = new StackFrame(0, true);
                    var fileName = sf.GetFileName();
                    var sourceWebRootDirectory = string.IsNullOrEmpty(fileName)
                                                     ? ""
                                                     : Path.GetFullPath(Path.Combine(fileName, @"..\..\..", debugPath));
                    fileSystemWebRoot = Directory.Exists(sourceWebRootDirectory)
                                            ? sourceWebRootDirectory
                                            : Locations.WebContentDirectory;
                }
                else
                {
                    fileSystemWebRoot = Locations.WebContentDirectory;
                }
            }
            catch (Exception)
            {
                fileSystemWebRoot = Locations.WebContentDirectory;
            }
            return fileSystemWebRoot;
        }
    }
}