﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Illumina.TerminalVelocity
{
    public class SimpleHttpGetByRangeClient : ISimpleHttpGetByRangeClient
    {
        public const string REQUEST_TEMPLATE = @"GET {0} HTTP/1.1
Host: {1}
Connection: keep-alive
Cache-Control: no-cache
User-Agent: Mozilla/5.0 (Windows NT 6.2; WOW64) AppleWebKit/537.22 (KHTML, like Gecko) Chrome/25.0.1364.97 Safari/537.22
Accept: */*
Accept-Encoding: gzip
Accept-Language: en-US,en;q=0.8
Accept-Charset: utf-8;q=0.7,*;q=0.3
Range: bytes={2}-{3}

";
        public static readonly byte[] BODY_INDICATOR = new byte[] {13, 10, 13, 10};
        public const int BUFFER_SIZE = 1024*8;
        public const int DEFAULT_TIMEOUT = 1000 * 200; //200 seconds
        private TcpClient tcpClient;
        private Uri baseUri;
        private Stream stream;
        private readonly BufferManager bufferManager;
        private int timeout;
        

        public SimpleHttpGetByRangeClient(Uri baseUri, BufferManager bufferManager = null, int timeout = DEFAULT_TIMEOUT)
        {
            this.baseUri = baseUri;
            tcpClient = new TcpClient();
            if (bufferManager == null)
            {
                bufferManager = new BufferManager(new[] { new BufferQueueSetting(BUFFER_SIZE, 1) });
            }
            this.bufferManager = bufferManager;
            this.timeout = timeout;
        }

        public SimpleHttpResponse Get(long start, long length)
        {
            return Get(baseUri, start, length);
        }

        public SimpleHttpResponse Get(Uri uri, long start, long length)
        {
            EnsureConnection(uri);

            byte[] request = Encoding.UTF8.GetBytes(BuildHttpRequest(uri, start, length));
            stream.Write(request, 0, request.Length);

            SimpleHttpResponse response = ParseResult(stream, length);

            return response;
        }

        public void Dispose()
        {
            if (tcpClient != null && tcpClient.Connected)
            {
                tcpClient.Close();
            }
            if (stream != null)
            {
                stream.Dispose();
            }
        }

        public int ConnectionTimeout
        {
            get { return timeout; }
            set
            {
                if (tcpClient != null)
                {
                    tcpClient.ReceiveTimeout = value;
                }
                timeout = value;
            }
        }

        protected void EnsureConnection(Uri uri)
        {
            if (uri.Host != baseUri.Host || (tcpClient.Connected && stream == null) )
            {
                if (stream != null)
                {
                    stream.Dispose();
                }
                if (tcpClient.Connected)
                {
                    tcpClient.Close();
                    tcpClient = new TcpClient();
                    
                }
                baseUri = uri;
            }

            if (!tcpClient.Connected)
            {
                
                tcpClient.ReceiveTimeout =timeout; 
                tcpClient.Connect(baseUri.Host, baseUri.Port);
                if (baseUri.Port == 443)
                {
                    var sslStream = new SslStream(tcpClient.GetStream());
                    sslStream.AuthenticateAsClient(baseUri.Host);
                    stream = sslStream;
                }
                else
                {
                    stream = tcpClient.GetStream();
                }
            }
           
        }


        internal static string BuildHttpRequest(Uri uri, long start, long length)
        {
            return string.Format(REQUEST_TEMPLATE, uri.AbsoluteUri, uri.Host, start, start + length - 1);
        }

        public SimpleHttpResponse ParseResult(Stream stream, long length)
        {
            var buffer = bufferManager.GetBuffer(BUFFER_SIZE);
            Dictionary<string, string> headers;
            int statusCode;
            byte[] headerData;
            int bodyStarts = -1;
            int bytesread = 0;

            using (var ms = new MemoryStream())
            {
                bytesread = stream.Read(buffer, 0, buffer.Length);
                ms.Write(buffer, 0, bytesread);
                //some calculations to determine how much data we are getting;
                int bodyIndex = buffer.IndexOf(BODY_INDICATOR);
                bodyStarts = bodyIndex + BODY_INDICATOR.Length;

                headers = HttpParser.GetHttpHeaders(buffer, bodyIndex, out statusCode);
                headerData = ms.ToArray();
            }

            if (statusCode >= 200 && statusCode <= 300)
            {

                long contentLength = long.Parse(headers[HttpParser.HttpHeaders.ContentLength]);

                var dest = bufferManager.GetBuffer((uint)contentLength);
                using (var outputStream = new MemoryStream(dest))
                {
                    int destPlace = headerData.Length - bodyStarts;
                    long totalContent = contentLength + bodyStarts;
                    long left = totalContent - bytesread;

                    outputStream.Write(headerData, bodyStarts, destPlace);

                    while (left > 0)
                    {
                        // Trace.WriteLine(string.Format("reading buffer {0}", (int)(left < buffer.Length ? left : buffer.Length)));
                        bytesread = stream.Read(buffer, 0, (int) (left < buffer.Length ? left : buffer.Length));
                        outputStream.Write(buffer, 0, bytesread);
                        left -= bytesread;
                    }
                    bufferManager.FreeBuffer(buffer);
                    return new SimpleHttpResponse(statusCode, dest, headers);
                }
            }
            bufferManager.FreeBuffer(buffer);
            return new SimpleHttpResponse(statusCode, null, headers);
        }
    }
}