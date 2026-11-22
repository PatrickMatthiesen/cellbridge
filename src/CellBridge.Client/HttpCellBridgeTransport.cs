using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;

namespace CellBridge.Client;

/// <summary>Executes SOAP and MTOM requests through a caller-owned HTTP client.</summary>
/// <remarks>Configure bearer authentication or a cookie container on the HTTP client.
/// Disable automatic redirects on its handler to keep ticket requests on the configured origin.</remarks>
public sealed class HttpCellBridgeTransport : ICellBridgeClientTransport
{
    private readonly HttpClient _client;
    private readonly Uri _origin;
    private readonly Uri _endpoint;
    private readonly long _maxResponseBytes;

    public HttpCellBridgeTransport(HttpClient client, Uri publicOrigin, string applicationPathBase = "",
        long maxResponseBytes = 192L * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(publicOrigin);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResponseBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxResponseBytes, Array.MaxLength);
        if (!publicOrigin.IsAbsoluteUri || publicOrigin.Scheme is not ("http" or "https") ||
            publicOrigin.AbsolutePath != "/" || publicOrigin.UserInfo.Length != 0 ||
            publicOrigin.Query.Length != 0 || publicOrigin.Fragment.Length != 0)
            throw new ArgumentException("An HTTP(S) origin without credentials, path, query or fragment is required.", nameof(publicOrigin));

        var mount = EncodeMount(applicationPathBase);
        _client = client;
        _origin = publicOrigin;
        _maxResponseBytes = maxResponseBytes;
        _endpoint = new Uri(_origin, mount + "/_vti_bin/cellstorage.svc");
    }

    internal static string EncodeMount(string applicationPathBase)
    {
        var segments = applicationPathBase.Split('/');
        if (applicationPathBase.Length != 0 &&
            (segments[0].Length != 0 || segments.Skip(1).Any(segment =>
                segment.Length == 0 || segment is "." or ".." || segment.Any(c => char.IsControl(c) || c is '\\' or '?' or '#'))))
            throw new ArgumentException("A decoded application mount path without a trailing slash is required.", nameof(applicationPathBase));

        return string.Join('/', segments.Select(Uri.EscapeDataString));
    }

    public async Task<CellStorageExecution> ExecuteAsync(CellStorageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        if (request.Requests.SelectMany(file => file.SubRequests).Any(sub => sub.SubRequestDataBinaryMemory.HasValue))
        {
            message.Content = new MtomContent(request.PrepareMtomMessage());
        }
        else
        {
            message.Content = new StringContent(request.ToSoapEnvelope(), Encoding.UTF8);
            message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.SoapContentType);
        }
        message.Headers.TryAddWithoutValidation("SOAPAction", $"\"{CellStorageRequest.ExecuteAction}\"");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_client.Timeout);
        var ct = deadline.Token;
        try
        {
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
                response.EnsureSuccessStatusCode();
            var contentType = response.Content.Headers.ContentType;
            if (contentType?.MediaType?.ToLowerInvariant() is not ("text/xml" or "application/soap+xml" or "multipart/related"))
            {
                response.EnsureSuccessStatusCode();
                throw new InvalidDataException("The server did not return a SOAP or MTOM response.");
            }

            await response.Content.LoadIntoBufferAsync(_maxResponseBytes, ct).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var parsed = CellStorageResponseParser.Parse(bytes, contentType.ToString(), request);
            if (parsed.Fault is null)
                response.EnsureSuccessStatusCode();
            return new(parsed, []);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("The CellBridge request timed out.", error);
        }
    }

    public Task<ICellBridgeRealTimeChannel?> TryConnectRealTimeAsync(RealTimeConnectionTarget target,
        CancellationToken cancellationToken = default) =>
        ClassicSignalRChannel.TryConnectAsync(_client, _origin, target, cancellationToken);

    private sealed class MtomContent : HttpContent
    {
        private readonly MtomResponseMessage _message;

        public MtomContent(MtomResponseMessage message)
        {
            _message = message;
            Headers.ContentType = MediaTypeHeaderValue.Parse(message.ContentType);
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            _message.WriteToAsync(stream).AsTask();

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context,
            CancellationToken cancellationToken) => _message.WriteToAsync(stream, cancellationToken).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = _message.ContentLength;
            return true;
        }
    }
}
