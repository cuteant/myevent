﻿using System.Net;

namespace EventStore.ClientAPI.Transport.Http
{
  internal static class EndpointExtensions
  {
    public const string HTTP_SCHEMA = "http";
    public const string HTTPS_SCHEMA = "https";

    public static string ToHttpUrl(this EndPoint endPoint, string schema, string rawUrl = null)
    {
      if (endPoint is IPEndPoint ipEndPoint)
      {
        return CreateHttpUrl(schema, ipEndPoint.ToString(), rawUrl is object ? rawUrl.TrimStart('/') : string.Empty);
      }
      if (endPoint is DnsEndPoint dnsEndpoint)
      {
        return CreateHttpUrl(schema, dnsEndpoint.Host, dnsEndpoint.Port, rawUrl is object ? rawUrl.TrimStart('/') : string.Empty);
      }
      return null;
    }

    public static string ToHttpUrl(this EndPoint endPoint, string schema, string formatString, params object[] args)
    {
      if (endPoint is IPEndPoint ipEndPoint)
      {
        return CreateHttpUrl(schema, ipEndPoint.ToString(), string.Format(formatString.TrimStart('/'), args));
      }
      if (endPoint is DnsEndPoint dnsEndpoint)
      {
        return CreateHttpUrl(schema, dnsEndpoint.Host, dnsEndpoint.Port, string.Format(formatString.TrimStart('/'), args));
      }
      return null;
    }

    private static string CreateHttpUrl(string schema, string host, int port, string path)
    {
      return $"{schema}://{host}:{port}/{path}";
    }

    private static string CreateHttpUrl(string schema, string address, string path)
    {
      return $"{schema}://{address}/{path}";
    }
  }
}
