using System.Threading;
using System.Threading.Tasks;
using Itech.Emailing.Providers.Models;

namespace Itech.Emailing.Providers;

internal interface IEmailProvider
{
    Task<SendResult> SendAsync(SendRequest request, CancellationToken ct);
}
