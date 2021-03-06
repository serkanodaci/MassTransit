namespace MassTransit.Pipeline.Filters
{
    using System;
    using System.Linq;
    using System.Threading.Tasks;
    using Context;
    using Events;
    using GreenPipes;
    using Metadata;


    /// <summary>
    /// Generates and publishes a <see cref="Fault"/> event for the exception
    /// </summary>
    public class GenerateFaultFilter :
        IFilter<ExceptionReceiveContext>
    {
        void IProbeSite.Probe(ProbeContext context)
        {
            context.CreateFilterScope("generateFault");
        }

        async Task IFilter<ExceptionReceiveContext>.Send(ExceptionReceiveContext context, IPipe<ExceptionReceiveContext> next)
        {
            if (!context.IsFaulted)
            {
                await GenerateFault(context).ConfigureAwait(false);

                await context.NotifyFaulted(context.Exception).ConfigureAwait(false);
            }

            await next.Send(context).ConfigureAwait(false);
        }

        static async Task GenerateFault(ExceptionReceiveContext context)
        {
            Guid? messageId;
            Guid? requestId;
            string[] messageTypes = null;

            if (context.TryGetPayload(out ConsumeContext consumeContext))
            {
                messageId = consumeContext.MessageId;
                requestId = consumeContext.RequestId;
                messageTypes = consumeContext.SupportedMessageTypes.ToArray();
            }
            else
            {
                messageId = context.TransportHeaders.Get("MessageId", default(Guid?));
                requestId = context.TransportHeaders.Get("RequestId", default(Guid?));
            }

            ReceiveFault fault = new ReceiveFaultEvent(HostMetadataCache.Host, context.Exception, context.ContentType.MediaType, messageId, messageTypes);

            var faultEndpoint = await GetFaultEndpoint(context, consumeContext, requestId).ConfigureAwait(false);

            await faultEndpoint.Send(fault).ConfigureAwait(false);
        }

        static async Task<ISendEndpoint> GetFaultEndpoint(ReceiveContext context, ConsumeContext consumeContext, Guid? requestId)
        {
            Task ConsumeTask(Task task)
            {
                context.AddReceiveTask(task);
                return task;
            }

            var destinationAddress = consumeContext?.FaultAddress ?? consumeContext?.ResponseAddress;
            if (destinationAddress != null)
            {
                var sendEndpoint = await context.SendEndpointProvider.GetSendEndpoint(destinationAddress).ConfigureAwait(false);

                return new ConsumeSendEndpoint(sendEndpoint, consumeContext, ConsumeTask, requestId);
            }

            var publishSendEndpoint = await context.PublishEndpointProvider.GetPublishSendEndpoint<ReceiveFault>().ConfigureAwait(false);

            if (consumeContext != null)
                return new ConsumeSendEndpoint(publishSendEndpoint, consumeContext, ConsumeTask, requestId);

            return publishSendEndpoint;
        }
    }
}
