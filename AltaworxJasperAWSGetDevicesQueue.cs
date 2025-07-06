using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Altaworx.AWS.Core;
using Altaworx.AWS.Core.Helpers.Constants;
using Altaworx.AWS.Core.Models;
using Altaworx.AWS.Core.Services;
using Altaworx.AWS.Core.Services.Optimization;
using Amazon;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.SimpleEmail.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amop.Core.Logger;
using Amop.Core.Repositories.Jasper;
using Amop.Core.Repositories.Optimization;
using Amop.Core.Resilience;
using Amop.Core.Services.Jasper;
using Microsoft.Data.SqlClient;
using MimeKit;
using Newtonsoft.Json;
using Polly;
using Polly.Wrap;
using static Amazon.Lambda.SQSEvents.SQSEvent;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]
namespace AltaworxJasperAWSGetDevicesQueue
{
    public class Function : AwsFunctionBase
    {
        private const int DefaultDelaySeconds = 5;
        private const int SQL_TRANSIENT_RETRY_MAX_COUNT = 3;
        private const int GENERAL_TRANSIENT_RETRY_MAX_COUNT = 3;
        private const int GENERAL_TRANSIENT_RETRY_BASE_SECONDS = 2;
        private const int HTTP_TRANSIENT_RETRY_MAX_COUNT = 3;
        private const int MAX_HTTP_RETRY_FAILURE_COUNT = 5; // prevent continuing to call service in case of catastrophic outage or misconfiguration
        private const string EXCEPTION_MESSAGE_DEFAULT = "UNKNOWN ERROR";
        private string JasperDevicesGetPath = Environment.GetEnvironmentVariable("JasperDevicesGetPath");
        private string DestinationQueueURL = Environment.GetEnvironmentVariable("DestinationQueueURL");
        private string ExportDeviceUsageQueueURL = Environment.GetEnvironmentVariable("ExportDeviceUsageQueueURL");
        private string OptimizationUsageQueueURL = Environment.GetEnvironmentVariable("OptimizationUsageQueueURL");
        private string RatePlanUpdateQueueURL = Environment.GetEnvironmentVariable("RatePlanUpdateQueueURL");
        private int MaxPagesToProcess = Convert.ToInt32(Environment.GetEnvironmentVariable("MaxPagesToProcess"));
        private string AWSEnv = Environment.GetEnvironmentVariable("AWSEnv");

        public async Task FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
        {
            KeySysLambdaContext keysysContext = null;
            try
            {
                keysysContext = BaseFunctionHandler(context);

                if (string.IsNullOrEmpty(DestinationQueueURL))
                {
                    DestinationQueueURL = context.ClientContext.Environment["DestinationQueueURL"];
                    JasperDevicesGetPath = context.ClientContext.Environment["JasperDevicesGetPath"];
                    ExportDeviceUsageQueueURL = context.ClientContext.Environment["ExportDeviceUsageQueueURL"];
                    OptimizationUsageQueueURL = context.ClientContext.Environment["OptimizationUsageQueueURL"];
                    RatePlanUpdateQueueURL = context.ClientContext.Environment["RatePlanUpdateQueueURL"];
                    MaxPagesToProcess = Convert.ToInt32(context.ClientContext.Environment["MaxPagesToProcess"]);
                    AWSEnv = context.ClientContext.Environment["AWSEnv"];
                }

                LogInfo(keysysContext, "STATUS", $"Beginning to process {sqsEvent.Records.Count} records...");

                foreach (var record in sqsEvent.Records)
                {
                    LogInfo(keysysContext, "MessageId", record.MessageId);
                    LogInfo(keysysContext, "EventSource", record.EventSource);
                    LogInfo(keysysContext, "Body", record.Body);

                    var sqsValues = GetMessageQueueValues(keysysContext, record); //Sets pagenumber and lastsyncdate values

                    var jasperAuth = JasperCommon.GetJasperAuthenticationInformation(keysysContext.CentralDbConnectionString, sqsValues.ServiceProviderId);

                    if (jasperAuth != null)
                    {
                        try
                        {
                            if (sqsValues.PageNumber == 1)
                            {
                                ClearJasperDeviceStagingWithPolicy(keysysContext, sqsValues);
                            }

                            await ProcessDeviceList(keysysContext, sqsValues, jasperAuth);

                            if (sqsValues.Errors.Count > 0)
                            {
                                if (sqsValues.OptimizationSessionId != null
                                    && sqsValues.OptimizationSessionId > 0)
                                {
                                    await OptimizationUsageSyncErrorHandler.ProcessStopCarrierOptimization(keysysContext, sqsValues.ServiceProviderId, sqsValues.OptimizationSessionId.Value, string.Join(Environment.NewLine, sqsValues.Errors));
                                }
                                else
                                {
                                    await SendErrorEmailNotificationAsync(keysysContext, sqsValues);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            LogInfo(keysysContext, "EXCEPTION", ex.Message + " " + ex.StackTrace);
                        }
                    }
                    else
                    {
                        LogInfo(keysysContext, "WARN", $"Not Processed. No Auth for Provider {sqsValues.ServiceProviderId}");
                    }

                }

                LogInfo(keysysContext, "STATUS", $"Processed {sqsEvent.Records.Count} records.");
            }
            catch (Exception ex)
            {
                // use lambda logger in case exception occurs in creating original context object
                context.Logger.LogLine($"EXCEPTION: {ex.Message} {ex.StackTrace}");
            }

            keysysContext?.CleanUp();
        }

        private async Task ProcessDeviceList(KeySysLambdaContext context, GetDeviceQueueSqsValues sqsValues, JasperAuthentication jasperAuth)
        {
            LogInfo(context, "JasperAPIUsername", jasperAuth.Username);
            LogInfo(context, "LastSyncDate", sqsValues.LastSyncDate);
            LogInfo(context, "PageNumber", sqsValues.PageNumber);

            bool isLastPage = false;
            while (sqsValues.PageCounter <= MaxPagesToProcess && sqsValues.Errors.Count <= MAX_HTTP_RETRY_FAILURE_COUNT)
            {
                if (!isLastPage)
                {
                    isLastPage = await GetJasperDevicesWithPolicy(context, sqsValues, jasperAuth, sqsValues.LastSyncDate, sqsValues.PageNumber);
                    sqsValues.PageCounter++;
                    sqsValues.PageNumber++;
                }
                else
                {
                    break;
                }
            }

            if (sqsValues.Errors.Count > MAX_HTTP_RETRY_FAILURE_COUNT)
            {
                LogInfo(context, "DEBUG", $"Exceeded maximum of {MAX_HTTP_RETRY_FAILURE_COUNT} HTTP retry failures - will not attempt to sync any more devices. Continuing with next step: {sqsValues.NextStep:G}");
                isLastPage = true;
            }

            sqsValues.PageCounter = 1;

            LogInfo(context, "isLastPage", isLastPage);

            DataTable table = new DataTable();
            table.Columns.Add("ID");
            table.Columns.Add("ICCID");
            table.Columns.Add("Status");
            table.Columns.Add("RatePlan");
            table.Columns.Add("CommunicationPlan");
            table.Columns.Add("CreatedBy");
            table.Columns.Add("CreatedDate");
            table.Columns.Add("ServiceProviderId");

            if (sqsValues.JasperDeviceList.Count > 0)
            {
                foreach (var jasperDevice in sqsValues.JasperDeviceList)
                {
                    var dr = AddToDataRow(table, jasperDevice);
                    table.Rows.Add(dr);
                }
                LogInfo(context, "STATUS", "SQL Bulk Copy Start");

                var sqlBulkCopyRetryPolicy = GetSqlTransientPolicy(context.logger, sqsValues,
                    $"AltaworxJasperAWSGetDevicesQueue::ProcessDeviceList::SqlBulkCopy:JasperDeviceStaging");
                sqlBulkCopyRetryPolicy.Execute(() => SqlBulkCopy(context, context.GeneralProviderSettings.JasperDbConnectionString, table, "JasperDeviceStaging"));

                var sqlUpdateJasperDevicesRetryPolicy = GetSqlTransientPolicy(context.logger, sqsValues,
                    $"AltaworxJasperAWSGetDevicesQueue::ProcessDeviceList::UpdateJasperDevices");
                LogInfo(context, "STATUS", "Jasper Devices update done through Stored Procedure");
                sqlUpdateJasperDevicesRetryPolicy.Execute(() => UpdateJasperDevices(context, isLastPage, sqsValues.ServiceProviderId, jasperAuth));
            }

            if (!isLastPage)
            {
                var generalRetryPolicy = GetGeneralTransientPolicy(context.logger, sqsValues,
                    $"AltaworxJasperAWSGetDevicesQueue::ProcessDeviceList::SendMessageToQueue: Queue URL: {DestinationQueueURL}");
                await generalRetryPolicy.ExecuteAsync(async () => await SendMessageToQueue(context, sqsValues, DestinationQueueURL));
            }
            else
            {
                // check if full usage sync or usage by rate plan sync
                var generalRetryPolicy = GetGeneralTransientPolicy(context.logger, sqsValues,
                    $"AltaworxJasperAWSGetDevicesQueue::ProcessDeviceList::ProcessNextStep: Next Step: {sqsValues.NextStep}");
                await generalRetryPolicy.ExecuteAsync(async () => await ProcessNextStep(context, sqsValues));
            }
        }

        private async Task ProcessNextStep(KeySysLambdaContext context, GetDeviceQueueSqsValues sqsValues)
        {
            switch (sqsValues.NextStep)
            {
                case JasperDeviceSyncNextStep.DeviceUsageByRatePlan:
                    await SendMessageToDeviceUsageByRatePlanQueue(context, OptimizationUsageQueueURL, sqsValues);
                    break;
                case JasperDeviceSyncNextStep.DeviceUsageExport:
                    await SendMessageToGetExportDeviceUsageQueueAsync(context, sqsValues, ExportDeviceUsageQueueURL);
                    break;
                case JasperDeviceSyncNextStep.UpdateDeviceRatePlan:
                    await SendMessageToUpdateRatePlanQueueAsync(context, sqsValues, RatePlanUpdateQueueURL);
                    break;
                default:
                    LogInfo(context, "EXCEPTION", $"Unknown usage sync type: {sqsValues.NextStep}");
                    break;
            }
        }


        private async Task<bool> GetJasperDevicesWithPolicy(KeySysLambdaContext context, GetDeviceQueueSqsValues sqsValues,
            JasperAuthentication jasperAuth, DateTime lastSyncDate, int pageNumber)
        {
            var httpRetryPolicy = GetHttpTransientPolicy(context.logger, sqsValues,
                $"AltaworxJasperAWSGetDevicesQueue::ProcessDeviceList::GetJasperDevices");
            return await httpRetryPolicy.ExecuteAsync(() =>
                GetJasperDevices(context, sqsValues, jasperAuth, lastSyncDate, pageNumber));
        }

        private async Task<bool> GetJasperDevices(KeySysLambdaContext context, GetDeviceQueueSqsValues sqsValues, JasperAuthentication jasperAuth, DateTime lastSyncDate, int pageNumber)
        {
            bool isLastPage = false;
            var decodedPassword = context.Base64Service.Base64Decode(jasperAuth.Password);
            using (HttpClient client = new HttpClient(new LambdaLoggingHandler()))
            {
                client.BaseAddress = new Uri($"{jasperAuth.ProductionApiUrl.TrimEnd('/')}/{JasperDevicesGetPath.TrimStart('/')}?modifiedSince={lastSyncDate:s}Z&pageNumber={pageNumber}");
                LogInfo(context, "Endpoint", client.BaseAddress);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var encoded = context.Base64Service.Base64Encode(jasperAuth.Username + ":" + decodedPassword);
                client.DefaultRequestHeaders.Add("Authorization", "Basic " + encoded);
                HttpResponseMessage response = await client.GetAsync(client.BaseAddress);
                response.EnsureSuccessStatusCode();

                if (response.IsSuccessStatusCode)
                {
                    string responseBody = await response.Content.ReadAsStringAsync();
                    var jasperDevices = JsonConvert.DeserializeObject<RootObject>(responseBody);

                    LogInfo(context, "JasperDeviceCount", jasperDevices.devices.Count);
                    if (jasperDevices.devices.Count > 0)
                    {
                        foreach (var jasperDevice in jasperDevices.devices)
                        {
                            Device dev = new Device
                            {
                                iccid = jasperDevice.iccid,
                                status = jasperDevice.status,
                                communicationPlan = jasperDevice.communicationPlan,
                                ratePlan = jasperDevice.ratePlan,
                                serviceProviderId = sqsValues.ServiceProviderId
                            };

                            if (!sqsValues.JasperDeviceList.Any(x => x.iccid == jasperDevice.iccid))
                            {
                                sqsValues.JasperDeviceList.Add(dev);
                            }
                        }
                    }
                    if (jasperDevices.lastPage)
                    {
                        isLastPage = true;
                    }

                    LogInfo(context, "isLastPage", isLastPage);
                }
                else
                {
                    string responseBody = await response.Content.ReadAsStringAsync();
                    isLastPage = true;
                    LogInfo(context, "EXCEPTION", responseBody);
                }
            }

            return isLastPage;
        }

        private static DataRow AddToDataRow(DataTable table, Device device)
        {
            var dr = table.NewRow();
            dr[1] = device.iccid;
            dr[2] = device.status;
            dr[3] = device.ratePlan;
            dr[4] = device.communicationPlan;
            dr[5] = "AWS Lambda - Get Devices Service";
            dr[6] = DateTime.UtcNow;
            dr[7] = device.serviceProviderId;
            return dr;
        }

        private static void UpdateJasperDevices(KeySysLambdaContext context, bool isLastPage, int serviceProviderId, JasperAuthentication jasperAuth)
        {

            var serviceProvider = ServiceProviderCommon.GetServiceProvider(context.CentralDbConnectionString, serviceProviderId);

            using (var centralConn = new SqlConnection(context.CentralDbConnectionString))
            {
                using (var conn = new SqlConnection(context.GeneralProviderSettings.JasperDbConnectionString))
                {
                    using (var cmd = new SqlCommand("usp_Update_Jasper_Device", conn))
                    {
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Parameters.Add(new SqlParameter("@isLastPage", isLastPage));
                        cmd.Parameters.AddWithValue("@BillingCycleEndDay", jasperAuth.BillingPeriodEndDay);
                        cmd.Parameters.AddWithValue("@BillingCycleEndHour", jasperAuth.BillingPeriodEndHour == null ? 0 : jasperAuth.BillingPeriodEndHour);
                        cmd.Parameters.AddWithValue("@CentralDbName", centralConn.Database);
                        cmd.Parameters.AddWithValue("@ServiceProviderId", serviceProviderId);
                        cmd.Parameters.AddWithValue("@IntegrationId", serviceProvider.IntegrationId);
                        conn.Open();
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        private void ClearJasperDeviceStagingWithPolicy(KeySysLambdaContext context, GetDeviceQueueSqsValues sqsValues)
        {
            var deviceStagingRepo = new JasperDeviceStagingRepository();
            deviceStagingRepo.DeleteStagingWithPolicy(context.logger,
                context.GeneralProviderSettings.JasperDbConnectionString,
                sqsValues.ServiceProviderId,
                sqsValues.Errors);
        }

        private async Task<string> GetServiceProviderWithPolicy(KeySysLambdaContext context, GetDeviceQueueSqsValues sqsValues)
        {
            var fallbackPolicy = Policy<string>
                .Handle<Exception>()
                .FallbackAsync(async cancellationToken => await Task.FromResult(sqsValues.ServiceProviderId.ToString()));

            var sqlTransientRetryPolicy = GetSqlAsyncPolicy(context.logger);
            return await fallbackPolicy.WrapAsync(sqlTransientRetryPolicy).ExecuteAsync(() =>
                GetServiceProvider(context.CentralDbConnectionString, sqsValues.ServiceProviderId));
        }

        private async Task<string> GetServiceProvider(string connectionString, int serviceProviderId)
        {
            var serviceProvider = string.Empty;
            using (var connection = new SqlConnection(connectionString))
            {
                using (var command = new SqlCommand("Select DisplayName FROM ServiceProvider WHERE id = @serviceProviderId", connection))
                {
                    command.CommandType = CommandType.Text;
                    command.Parameters.AddWithValue("@serviceProviderId", serviceProviderId);
                    connection.Open();

                    var rdr = await command.ExecuteReaderAsync();
                    while (rdr.Read())
                    {
                        serviceProvider = rdr["DisplayName"].ToString();
                    }
                }
            }

            return serviceProvider;
        }
        private class RootObject
        {
            public int pageNumber { get; set; }
            public List<Device> devices { get; set; }
            public bool lastPage { get; set; }
        }

        private static GetDeviceQueueSqsValues GetMessageQueueValues(KeySysLambdaContext context, SQSMessage message)
        {
            return new GetDeviceQueueSqsValues(context, message);
        }

        private async Task SendMessageToQueue(KeySysLambdaContext context, GetDeviceQueueSqsValues sqsValues, string destinationQueueURL)
        {
            LogInfo(context, "SUB", "SendMessageToQueue");
            LogInfo(context, "PageNumber", sqsValues.PageNumber);
            LogInfo(context, "LastSyncDate", sqsValues.LastSyncDate);
            LogInfo(context, "DestinationQueueURL", destinationQueueURL);
            LogInfo(context, "OptimizationSessionId", sqsValues.OptimizationSessionId);

            if (string.IsNullOrWhiteSpace(destinationQueueURL))
            {
                return; // to be able to skip enqueuing messages in a test
            }

            var awsCredentials = AwsCredentials(context);
            using (var client = new AmazonSQSClient(awsCredentials, RegionEndpoint.USEast1))
            {
                var requestMsgBody = $"Next page number is {sqsValues.PageNumber} for the last sync date {sqsValues.LastSyncDate}";
                LogInfo(context, "Sending message for", $"{requestMsgBody} to destination queue: {destinationQueueURL}");

                var request = new SendMessageRequest
                {
                    DelaySeconds = DefaultDelaySeconds,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        {
                            "PageNumber", new MessageAttributeValue
                            { DataType = "String", StringValue = sqsValues.PageNumber.ToString()}
                        },
                        {
                            "LastSyncDate", new MessageAttributeValue
                            { DataType = "String", StringValue = sqsValues.LastSyncDate.ToString()}
                        },
                        {
                            "ServiceProviderId", new MessageAttributeValue
                                { DataType = "String", StringValue = sqsValues.ServiceProviderId.ToString()}
                        },
                        {
                            "NextStep", new MessageAttributeValue
                                { DataType = "String", StringValue = ((int)sqsValues.NextStep).ToString()}
                        }
                    },
                    MessageBody = requestMsgBody,
                    QueueUrl = destinationQueueURL
                };

                if (sqsValues.OptimizationInstanceId.HasValue)
                {
                    request.MessageAttributes.Add("OptimizationInstanceId",
                        new MessageAttributeValue { DataType = "Number", StringValue = sqsValues.OptimizationInstanceId.Value.ToString() });
                }

                if (sqsValues.OptimizationSessionId.HasValue)
                {
                    request.MessageAttributes.Add("OptimizationSessionId",
                        new MessageAttributeValue { DataType = "String", StringValue = sqsValues.OptimizationSessionId.Value.ToString() });
                }

                LogInfo(context, "STATUS", "SendMessageRequest is ready!");
                LogInfo(context, "MessageBody", request.MessageBody);
                LogInfo(context, "QueueURL", request.QueueUrl);
                LogInfo(context, "PageNumber", request.MessageAttributes["PageNumber"].StringValue);
                LogInfo(context, "LastSyncDate", request.MessageAttributes["LastSyncDate"].StringValue);

                var response = await client.SendMessageAsync(request);
                if (((int)response.HttpStatusCode < 200) || ((int)response.HttpStatusCode > 299))
                {
                    LogInfo(context, "EXCEPTION", $"Error enqueuing message to {destinationQueueURL}: {response.HttpStatusCode:d} {response.HttpStatusCode:g}");
                }
            }
        }

        private async Task SendMessageToDeviceUsageByRatePlanQueue(KeySysLambdaContext context, string optimizationUsageQueueURL, GetDeviceQueueSqsValues sqsValues, int currentRatePlanId = 0, int currentPageNumber = 1)
        {
            LogInfo(context, LogTypeConstant.Sub, $"SendMessageToDeviceUsageByRatePlanQueue(...{sqsValues.ServiceProviderId}, {currentRatePlanId}, {currentPageNumber})");
            LogInfo(context, "ServiceProviderId", sqsValues.ServiceProviderId);
            LogInfo(context, "OptimizationSessionId", sqsValues.OptimizationSessionId);
            LogInfo(context, "RatePlanId", currentRatePlanId);
            LogInfo(context, "PageNumber", currentPageNumber);
            LogInfo(context, "OptimizationUsageQueueURL", optimizationUsageQueueURL);

            if (string.IsNullOrWhiteSpace(optimizationUsageQueueURL))
            {
                return; // to be able to skip enqueuing messages in a test
            }

            var awsCredentials = AwsCredentials(context);
            using (var client = new AmazonSQSClient(awsCredentials, RegionEndpoint.USEast1))
            {
                var requestMsgBody = $"Get Optimization Usage for Service Provider {sqsValues.ServiceProviderId}";
                LogInfo(context, "Sending message for", $"{requestMsgBody} to destination queue: {optimizationUsageQueueURL}");
                var request = new SendMessageRequest
                {
                    DelaySeconds = DefaultDelaySeconds,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        {
                            "ServiceProviderId", new MessageAttributeValue
                                { DataType = "String", StringValue = sqsValues.ServiceProviderId.ToString()}
                        },
                        {
                            "RatePlanId", new MessageAttributeValue
                                { DataType = "String", StringValue = currentRatePlanId.ToString()}
                        },
                        {
                            "PageNumber", new MessageAttributeValue
                                { DataType = "String", StringValue = currentPageNumber.ToString()}
                        },
                        {
                            "Initialize", new MessageAttributeValue
                                { DataType = "String", StringValue = false.ToString()}
                        }
                    },
                    MessageBody = requestMsgBody,
                    QueueUrl = optimizationUsageQueueURL
                };

                if (sqsValues.OptimizationSessionId != null && sqsValues.OptimizationSessionId.Value > 0)
                {
                    request.MessageAttributes.Add("OptimizationSessionId", new MessageAttributeValue { DataType = "String", StringValue = sqsValues.OptimizationSessionId.Value.ToString() });
                }

                var response = await client.SendMessageAsync(request);
                if (((int)response.HttpStatusCode < 200) || ((int)response.HttpStatusCode > 299))
                {
                    LogInfo(context, "EXCEPTION", $"Error enqueuing message to {optimizationUsageQueueURL}: {response.HttpStatusCode:d} {response.HttpStatusCode:g}");
                }
            }
        }

        private async Task SendMessageToGetExportDeviceUsageQueueAsync(KeySysLambdaContext context, GetDeviceQueueSqsValues sqsValues, string exportDeviceUsageQueueURL)
        {
            var initializeProcessing = true;

            LogInfo(context, "SUB", "SendMessageToGetExportDeviceUsageQueueAsync");
            LogInfo(context, "InitializeProcessing", initializeProcessing.ToString());
            LogInfo(context, "ExportDeviceUsageQueueURL", exportDeviceUsageQueueURL);

            if (string.IsNullOrWhiteSpace(exportDeviceUsageQueueURL))
            {
                return; // to be able to skip enqueuing messages in a test
            }

            var awsCredentials = AwsCredentials(context);
            using (var client = new AmazonSQSClient(awsCredentials, RegionEndpoint.USEast1))
            {
                var requestMsgBody = $"Requesting email to process";
                LogInfo(context, "Sending message for", $"{requestMsgBody} to DeviceUsage queue: {exportDeviceUsageQueueURL}");

                var request = new SendMessageRequest
                {
                    DelaySeconds = DefaultDelaySeconds,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        {
                            "InitializeProcessing", new MessageAttributeValue
                            {
                                DataType = "String", StringValue = initializeProcessing.ToString()
                            }
                        },
                        {
                            "WaitCount", new MessageAttributeValue
                            {
                                DataType = "String", StringValue = 0.ToString()
                            }
                        },
                        {
                            "ServiceProviderId", new MessageAttributeValue
                            {
                                DataType = "String", StringValue = sqsValues.ServiceProviderId.ToString()
                            }
                        }
                    },
                    MessageBody = requestMsgBody,
                    QueueUrl = exportDeviceUsageQueueURL
                };
                LogInfo(context, "STATUS", "Check Export File is ready!");
                LogInfo(context, "MessageBody", request.MessageBody);
                LogInfo(context, "QueueURL", request.QueueUrl);
                LogInfo(context, "InitializeProcessing", request.MessageAttributes["InitializeProcessing"].StringValue);

                var response = await client.SendMessageAsync(request);
                if (((int)response.HttpStatusCode < 200) || ((int)response.HttpStatusCode > 299))
                {
                    LogInfo(context, "EXCEPTION", $"Error enqueuing message to {exportDeviceUsageQueueURL}: {response.HttpStatusCode:d} {response.HttpStatusCode:g}");
                }
            }
        }

        private async Task SendMessageToUpdateRatePlanQueueAsync(KeySysLambdaContext context, GetDeviceQueueSqsValues sqsValues, string queueUrl)
        {
            LogInfo(context, "SUB", "SendMessageToUpdateRatePlanQueueAsync");
            LogInfo(context, "RatePlanUpdateQueueURL", queueUrl);

            if (string.IsNullOrWhiteSpace(queueUrl))
            {
                return; // to be able to skip enqueuing messages in a test
            }

            var awsCredentials = AwsCredentials(context);
            using (var client = new AmazonSQSClient(awsCredentials, RegionEndpoint.USEast1))
            {
                var request = new SendMessageRequest
                {
                    DelaySeconds = DefaultDelaySeconds,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        {
                            "InstanceId", new MessageAttributeValue
                            {
                                DataType = "Number", StringValue = sqsValues.OptimizationInstanceId.ToString()
                            }
                        },
                        {
                            "SyncedDevices", new MessageAttributeValue
                            {
                                DataType = "String", StringValue = true.ToString()
                            }
                        }
                    },
                    MessageBody = "NOT USED",
                    QueueUrl = queueUrl
                };

                var response = await client.SendMessageAsync(request);
                if (((int)response.HttpStatusCode < 200) || ((int)response.HttpStatusCode > 299))
                {
                    LogInfo(context, "EXCEPTION", $"Error enqueuing message to {queueUrl}: {response.HttpStatusCode:d} {response.HttpStatusCode:g}");
                }
            }
        }

        private static ISyncPolicy GetSqlTransientPolicy(IKeysysLogger logger, GetDeviceQueueSqsValues sqsValues, string errorContext = "")
        {
            var fallbackPolicy = GetFallbackPolicy(sqsValues, errorContext);
            return fallbackPolicy.Wrap(GetSqlPolicy(logger));
        }

        private static AsyncPolicyWrap<bool> GetHttpTransientPolicy(IKeysysLogger logger, GetDeviceQueueSqsValues sqsValues, string errorContext = "")
        {
            var fallbackPolicy = GetHttpAsyncFallbackPolicy(sqsValues, errorContext);
            return fallbackPolicy.WrapAsync(GetHttpPolicy(logger));
        }

        private static IAsyncPolicy GetGeneralTransientPolicy(IKeysysLogger logger, GetDeviceQueueSqsValues sqsValues, string errorContext = "")
        {
            var fallbackPolicy = GetAsyncFallbackPolicy(sqsValues, errorContext);
            return fallbackPolicy.WrapAsync(GetGeneralPolicy(logger));
        }

        private static ISyncPolicy GetSqlPolicy(IKeysysLogger logger)
        {
            var policyFactory = new PolicyFactory(logger);
            return policyFactory.GetSqlRetryPolicy(SQL_TRANSIENT_RETRY_MAX_COUNT);
        }

        private static IAsyncPolicy GetSqlAsyncPolicy(IKeysysLogger logger)
        {
            var policyFactory = new PolicyFactory(logger);
            return policyFactory.GetSqlAsyncRetryPolicy(SQL_TRANSIENT_RETRY_MAX_COUNT);
        }

        private static IAsyncPolicy GetGeneralPolicy(IKeysysLogger logger)
        {
            return Policy
                .Handle<Exception>()
                .WaitAndRetryAsync(GENERAL_TRANSIENT_RETRY_MAX_COUNT,
                    retryAttempt => TimeSpan.FromSeconds(Math.Pow(GENERAL_TRANSIENT_RETRY_BASE_SECONDS, retryAttempt)),
                    (exception, timeSpan, retryCount, sqlContext) => logger.LogInfo("STATUS",
                        $"Encountered transient error - delaying for {timeSpan.TotalMilliseconds}ms, then making retry {retryCount} out of {GENERAL_TRANSIENT_RETRY_MAX_COUNT}. Exception: {exception?.Message}"));
        }

        private static ISyncPolicy GetFallbackPolicy(GetDeviceQueueSqsValues sqsValues, string errorContext)
        {
            return Policy
                .Handle<Exception>()
                .Fallback(
                    (exception, context, token) => { },
                    (exception, context) =>
                    {
                        sqsValues.Errors.Add($"{errorContext} {exception?.Message ?? EXCEPTION_MESSAGE_DEFAULT}");
                    }
                );
        }

        private static IAsyncPolicy GetAsyncFallbackPolicy(GetDeviceQueueSqsValues sqsValues, string errorContext)
        {
            return Policy
                .Handle<Exception>()
                .FallbackAsync(
                    async (exception, context, token) => await Task.CompletedTask,
                    async (exception, context) =>
                    {
                        sqsValues.Errors.Add($"{errorContext} {exception?.Message ?? EXCEPTION_MESSAGE_DEFAULT}");
                        await Task.CompletedTask;
                    }
                );
        }

        private static IAsyncPolicy<bool> GetHttpAsyncFallbackPolicy(GetDeviceQueueSqsValues sqsValues, string errorContext)
        {
            return Policy<bool>
                .Handle<HttpRequestException>()
                .Or<TimeoutException>()
                .Or<OperationCanceledException>()
                .Or<TaskCanceledException>()
                .FallbackAsync(
                    async (outcome, context, token) => await Task.FromResult(false),
                    async (outcome, context) =>
                    {
                        sqsValues.Errors.Add($"{errorContext} {outcome?.Exception?.Message ?? EXCEPTION_MESSAGE_DEFAULT}");
                        await Task.CompletedTask;
                    }
                );
        }

        private static IAsyncPolicy GetHttpPolicy(IKeysysLogger logger)
        {
            var policyFactory = new PolicyFactory(logger);
            return policyFactory.GetHttpRetryPolicy(HTTP_TRANSIENT_RETRY_MAX_COUNT);
        }

        public async Task SendErrorEmailNotificationAsync(KeySysLambdaContext context, GetDeviceQueueSqsValues sqsValues)
        {
            LogInfo(context, "SUB", "SendErrorEmailNotificationAsync()");
            var serviceProvider = await GetServiceProviderWithPolicy(context, sqsValues);
            var bodyBuilder = BuildErrorEmailBody(context, serviceProvider, sqsValues.Errors);
            string subject = $"Error in Device Sync for {serviceProvider}";

            if (!context.IsProduction)
            {
                subject += $" ({AWSEnv})";
            }

            await Policy.Handle<Exception>()
                .WaitAndRetryAsync(GENERAL_TRANSIENT_RETRY_MAX_COUNT,
                    retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    (exception, timeSpan, retryCount, sqlContext) =>
                    {
                        LogInfo(context, "STATUS",
                            $"Encountered transient error sending error notification delaying for {timeSpan.TotalMilliseconds}ms, then making retry {retryCount}, out of {GENERAL_TRANSIENT_RETRY_MAX_COUNT}. Exception: {exception.Message}");
                    })
                .ExecuteAsync(async () => await SendEmailAsync(context, subject, bodyBuilder));
        }

        private async Task SendEmailAsync(KeySysLambdaContext context, string subject, BodyBuilder bodyBuilder)
        {
            LogInfo(context, "SUB", "SendEmailAsync()");
            var emailFactory = new SimpleEmailServiceFactory();
            using var client = emailFactory.getClient(AwsSesCredentials(context), RegionEndpoint.USEast1);
            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(context.GeneralProviderSettings.DeviceSyncFromEmailAddress));
            var recipientAddressList = context.GeneralProviderSettings.DeviceSyncToEmailAddresses.Split(';').ToList();
            foreach (var recipientAddress in recipientAddressList)
            {
                message.To.Add(MailboxAddress.Parse(recipientAddress));
            }

            message.Subject = subject;
            message.Body = bodyBuilder.ToMessageBody();
            using (var stream = new MemoryStream())
            {
                message.WriteTo(stream);

                var sendRequest = new SendRawEmailRequest
                {
                    RawMessage = new RawMessage(stream)
                };
                try
                {
                    var response = await client.SendRawEmailAsync(sendRequest);
                    LogInfo(context, "RESPONSE STATUS", $"{response.HttpStatusCode:d} {response.HttpStatusCode:g}");
                }
                catch (Exception ex)
                {
                    LogInfo(context, "EXCEPTION", "Error Sending Email: " + ex.Message);
                }
            }
        }

        private BodyBuilder BuildErrorEmailBody(KeySysLambdaContext context, string serviceProvider, ICollection<string> errorMessages)
        {
            LogInfo(context, "SUB", "BuildErrorEmailBody()");
            return new BodyBuilder
            {
                HtmlBody = @$"<html>
                    <h2>Device sync error</h2>
                    <p>
                        An error occurred while syncing devices for {serviceProvider}. Please refer to messages below and/or error logs for more information
                    </p>
                    <h3>Error messages:</h3>
                    <ul>
                        {string.Join("", errorMessages.Select(errorMessage => $"<li>{errorMessage}</li>"))}
                    </ul>
                </html>",
                TextBody = @$"
Device sync error
                    
An error occurred while syncing devices for {serviceProvider}. Please refer to messages below and/or error logs for more information

Error messages:

{string.Join(Environment.NewLine, errorMessages)}"
            };
        }
    }
}
