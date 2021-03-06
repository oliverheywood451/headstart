using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Headstart.Common.Helpers;
using Headstart.Models;
using Headstart.Models.Misc;
using Headstart.Models.Headstart;
using ordercloud.integrations.library;
using OrderCloud.SDK;
using System.IO;
using Headstart.Common.Services;
using Headstart.Common;
using ordercloud.integrations.exchangerates;
using Headstart.Common.Models;
using OrderCloud.Catalyst;

namespace Headstart.API.Commands
{
    public interface IEnvironmentSeedCommand
    {
        Task<EnvironmentSeedResponse> Seed(EnvironmentSeed seed);
        Task PostStagingRestore();
    }
    public class EnvironmentSeedCommand : IEnvironmentSeedCommand
    {
        private readonly IOrderCloudClient _oc;
        private readonly AppSettings _settings;
        private readonly IPortalService _portal;
        private readonly IHSSupplierCommand _supplierCommand;
        private readonly IHSBuyerCommand _buyerCommand;
        private readonly IExchangeRatesCommand _exhangeRates;
        private readonly IOrderCloudIntegrationsBlobService _translationsBlob;

        private readonly string _buyerApiClientName = "Default HeadStart Buyer UI";
        private readonly string _buyerLocalApiClientName = "Default HeadStart Buyer UI LOCAL"; // used for pointing integration events to the ngrok url
        private readonly string _sellerApiClientName = "Default HeadStart Admin UI";
        private readonly string _integrationsApiClientName = "Middleware Integrations";
        private readonly string _sellerUserName = "Default_Admin";
        private readonly string _fullAccessSecurityProfile = "DefaultContext";

        public EnvironmentSeedCommand(
            AppSettings settings,
            IPortalService portal,
            IHSSupplierCommand supplierCommand,
            IHSBuyerCommand buyerCommand,
            IOrderCloudClient oc,
            IExchangeRatesCommand exhangeRates
        )
        {
            _settings = settings;
            _portal = portal;
            _supplierCommand = supplierCommand;
            _buyerCommand = buyerCommand;
            _oc = oc;
            _exhangeRates = exhangeRates;
            var translationsConfig = new BlobServiceConfig()
            {
                ConnectionString = _settings.BlobSettings.ConnectionString,
                Container = _settings.BlobSettings.ContainerNameTranslations
            };
            _translationsBlob = new OrderCloudIntegrationsBlobService(translationsConfig);
        }

        public async Task<EnvironmentSeedResponse> Seed(EnvironmentSeed seed)
        {
            if (string.IsNullOrEmpty(_settings.OrderCloudSettings.ApiUrl))
            {
                throw new Exception("Missing required app setting OrderCloudSettings:ApiUrl");
            }
            if (string.IsNullOrEmpty(_settings.OrderCloudSettings.WebhookHashKey))
            {
                throw new Exception("Missing required app setting OrderCloudSettings:WebhookHashKey");
            }
            if (string.IsNullOrEmpty(_settings.EnvironmentSettings.MiddlewareBaseUrl))
            {
                throw new Exception("Missing required app setting EnvironmentSettings:MiddlewareBaseUrl");
            }

            var portalUserToken = await _portal.Login(seed.PortalUsername, seed.PortalPassword);
            await VerifyOrgExists(seed.SellerOrgID, portalUserToken);
            var orgToken = await _portal.GetOrgToken(seed.SellerOrgID, portalUserToken);

            await CreateDefaultSellerUsers(seed, orgToken);
            await CreateApiClients(orgToken);
            await CreateSecurityProfiles(orgToken);
            await AssignSecurityProfiles(seed, orgToken);

            var apiClients = await GetApiClients(orgToken);
            await CreateIncrementors(orgToken); // must be before CreateBuyers
            await CreateMessageSenders(seed, orgToken); // must be before CreateBuyers and CreateSuppliers

            await CreateBuyers(seed, orgToken);
            await CreateXPIndices(orgToken);
            await CreateAndAssignIntegrationEvents(new string[] { apiClients.BuyerUiApiClient.ID }, apiClients.BuyerLocalUiApiClient.ID, orgToken);
            await CreateSuppliers(seed, orgToken);

            // populates exchange rates into blob container name: settings.BlobSettings.ContainerNameExchangeRates or "currency" if setting is not defined
            await _exhangeRates.Update();

            // populate default english translations into blob container name: settings.BlobSettings.ContainerNameTranslations or "ngx-translate" if setting is not defined
            // provide other language files to support multiple languages
            var currentDirectory = Directory.GetCurrentDirectory();
            var englishTranslationsPath = Path.GetFullPath(Path.Combine(currentDirectory, @"..\Headstart.Common\Assets\english-translations.json"));
            await _translationsBlob.Save("i18n/en.json", File.ReadAllText(englishTranslationsPath));

            return new EnvironmentSeedResponse
            {
                Comments = "Success! Your environment is now seeded. The following clientIDs & secrets should be used to finalize the configuration of your application. The initial admin username and password can be used to sign into your admin application",
                ApiClients = new Dictionary<string, dynamic>
                {
                    ["Middleware"] = new
                    {
                        ClientID = apiClients.MiddlewareApiClient.ID,
                        ClientSecret = apiClients.MiddlewareApiClient.ClientSecret
                    },
                    ["Seller"] = new
                    {
                        ClientID = apiClients.AdminUiApiClient.ID
                    },
                    ["Buyer"] = new
                    {
                        ClientID = apiClients.BuyerUiApiClient.ID
                    }
                }
            };
        }

        public async Task VerifyOrgExists(string orgID, string devToken)
        {
            try
            {
                await _portal.GetOrganization(orgID, devToken);
            }
            catch
            {
                // the portal API no longer allows us to create a production organization outside of portal
                // though its possible to create on sandbox - for consistency sake we'll require its created before seeding
                throw new Exception("Failed to retrieve seller organization with SellerOrgID. The organization must exist before it can be seeded");
            }
        }

        /// <summary>
        /// The staging environment gets restored weekly from production
        /// during that restore things like webhooks, message senders, and integration events are shut off (so you don't for example email production customers)
        /// this process restores integration events which are required for checkout (with environment specific settings)
        public async Task PostStagingRestore()
        {
            var token = (await _oc.AuthenticateAsync()).AccessToken;
            var apiClients = await GetApiClients(token);
            var storefrontClientIDs = await GetStoreFrontClientIDs(token);

            var deleteIE = DeleteAllIntegrationEvents(token);
            await Task.WhenAll(deleteIE);

            // recreate with environment specific data
            var createIE = CreateAndAssignIntegrationEvents(storefrontClientIDs, apiClients.BuyerLocalUiApiClient.ID, token);
            var shutOffSupplierEmails = ShutOffSupplierEmailsAsync(token); // shut off email notifications for all suppliers

            await Task.WhenAll(createIE, shutOffSupplierEmails);
        }

        private async Task AssignSecurityProfiles(EnvironmentSeed seed, string orgToken)
        {
            // assign buyer security profiles
            var buyerSecurityProfileAssignmentRequests = seed.Buyers.Select(b =>
            {
                return _oc.SecurityProfiles.SaveAssignmentAsync(new SecurityProfileAssignment
                {
                    BuyerID = b.ID,
                    SecurityProfileID = CustomRole.HSBaseBuyer.ToString()
                }, orgToken);
            });
            await Task.WhenAll(buyerSecurityProfileAssignmentRequests);

            // assign seller security profiles to seller org
            var sellerSecurityProfileAssignmentRequests = SellerHsRoles.Select(role =>
            {
                return _oc.SecurityProfiles.SaveAssignmentAsync(new SecurityProfileAssignment()
                {
                    SecurityProfileID = role.ToString()
                }, orgToken);
            });
            await Task.WhenAll(sellerSecurityProfileAssignmentRequests);

            // assign full access security profile to default admin user
            var defaultAdminUser = (await _oc.AdminUsers.ListAsync(accessToken: orgToken)).Items.First(u => u.Username == _sellerUserName);
            await _oc.SecurityProfiles.SaveAssignmentAsync(new SecurityProfileAssignment()
            {
                SecurityProfileID = _fullAccessSecurityProfile,
                UserID = defaultAdminUser.ID
            }, orgToken);
        }

        private async Task CreateBuyers(EnvironmentSeed seed, string token)
        {
            seed.Buyers.Add(new HSBuyer
            {
                Name = "Default HeadStart Buyer",
                Active = true,
                xp = new BuyerXp
                {
                    MarkupPercent = 0
                }
            });
            foreach (var buyer in seed.Buyers)
            {
                var superBuyer = new SuperHSBuyer()
                {
                    Buyer = buyer,
                    Markup = new BuyerMarkup() { Percent = 0 }
                };

                var exists = await BuyerExistsAsync(buyer.Name, token);
                if(!exists)
                {
                    await _buyerCommand.Create(superBuyer, token, isSeedingEnvironment: true);
                }
            }
        }

        private async Task<bool> BuyerExistsAsync(string buyerName, string token)
        {
            var list = await _oc.Buyers.ListAsync(filters: new { Name = buyerName }, accessToken: token);
            return list.Items.Any();
        }

        private async Task CreateSuppliers(EnvironmentSeed seed, string token)
        {
            // Create Suppliers and necessary user groups and security profile assignments
            foreach (HSSupplier supplier in seed.Suppliers)
            {
                var exists = await SupplierExistsAsync(supplier.Name, token);
                if (!exists)
                {
                    await _supplierCommand.Create(supplier, token, isSeedingEnvironment: true);
                }
            }
        }

        private async Task<bool> SupplierExistsAsync(string supplierName, string token)
        {
            var list = await _oc.Suppliers.ListAsync(filters: new { Name = supplierName }, accessToken: token);
            return list.Items.Any();
        }

        private async Task CreateDefaultSellerUsers(EnvironmentSeed seed, string token)
        {
            // the middleware api client will use this user as the default context user
            var middlewareIntegrationsUser = new User
            {
                ID = "MiddlewareIntegrationsUser",
                Username = _sellerUserName,
                Email = "test@test.com",
                Active = true,
                FirstName = "Default",
                LastName = "User"
            };
            await _oc.AdminUsers.SaveAsync(middlewareIntegrationsUser.ID, middlewareIntegrationsUser, token);

            // used to log in immediately after seeding the organization
            var initialAdminUser = new User
            {
                ID = "InitialAdminUser",
                Username = seed.InitialAdminUsername,
                Password = seed.InitialAdminPassword,
                Email = "test@test.com",
                Active = true,
                FirstName = "Initial",
                LastName = "User"
            };
            await _oc.AdminUsers.SaveAsync(initialAdminUser.ID, initialAdminUser, token);
        }

        static readonly List<XpIndex> DefaultIndices = new List<XpIndex>() {
            new XpIndex { ThingType = XpThingType.UserGroup, Key = "Type" },
            new XpIndex { ThingType = XpThingType.UserGroup, Key = "Role" },
            new XpIndex { ThingType = XpThingType.UserGroup, Key = "Country" },
            new XpIndex { ThingType = XpThingType.Company, Key = "Data.ServiceCategory" },
            new XpIndex { ThingType = XpThingType.Company, Key = "Data.VendorLevel" },
            new XpIndex { ThingType = XpThingType.Company, Key = "SyncFreightPop" },
            new XpIndex { ThingType = XpThingType.Company, Key = "CountriesServicing" },
            new XpIndex { ThingType = XpThingType.Order, Key = "NeedsAttention" },
            new XpIndex { ThingType = XpThingType.Order, Key = "StopShipSync" },
            new XpIndex { ThingType = XpThingType.Order, Key = "OrderType" },
            new XpIndex { ThingType = XpThingType.Order, Key = "LocationID" },
            new XpIndex { ThingType = XpThingType.Order, Key = "SubmittedOrderStatus" },
            new XpIndex { ThingType = XpThingType.Order, Key = "IsResubmitting" },
            new XpIndex { ThingType = XpThingType.Order, Key = "SupplierIDs" },
            new XpIndex { ThingType = XpThingType.User, Key = "UserGroupID" },
            new XpIndex { ThingType = XpThingType.User, Key = "RequestInfoEmails" },
        };

        public async Task CreateXPIndices(string token)
        {
            foreach (var index in DefaultIndices)
            {
                //PutAsync is throwing id already exists error. Seems like it is trying to create.
                //That is why we are using try catch here
                //Bug in sdk?
                try
                {
                    await _oc.XpIndices.PutAsync(index, token);
                }
                catch (Exception ex) { }
            }
        }

        static readonly List<Incrementor> DefaultIncrementors = new List<Incrementor>() {
            new Incrementor { ID = "orderIncrementor", Name = "Order Incrementor", LastNumber = 0, LeftPaddingCount = 6 },
            new Incrementor { ID = "supplierIncrementor", Name = "Supplier Incrementor", LastNumber = 0, LeftPaddingCount = 3 },
            new Incrementor { ID = "buyerIncrementor", Name = "Buyer Incrementor", LastNumber = 0, LeftPaddingCount = 4 }
        };

        public async Task CreateIncrementors(string token)
        {
            foreach (var incrementor in DefaultIncrementors)
            {
                await _oc.Incrementors.SaveAsync(incrementor.ID, incrementor, token);
            }
        }

        private async Task<ApiClientIDs> GetApiClients(string token)
        {
            var list = await _oc.ApiClients.ListAllAsync(accessToken: token);
            var appNames = list.Select(x => x.AppName);
            var adminUIApiClient = list.First(a => a.AppName == _sellerApiClientName);
            var buyerUIApiClient = list.First(a => a.AppName == _buyerApiClientName);
            var buyerLocalUIApiClient = list.First(a => a.AppName == _buyerLocalApiClientName);
            var middlewareApiClient = list.First(a => a.AppName == _integrationsApiClientName);
            return new ApiClientIDs()
            {
                AdminUiApiClient = adminUIApiClient,
                BuyerUiApiClient = buyerUIApiClient,
                BuyerLocalUiApiClient = buyerLocalUIApiClient,
                MiddlewareApiClient = middlewareApiClient
            };
        }

        private async Task<string[]> GetStoreFrontClientIDs(string token)
        {
            var list = await _oc.ApiClients.ListAllAsync(filters: new { AppName = "Storefront - *" }, accessToken: token);
            return list.Select(client => client.ID).ToArray();
        }

        public class ApiClientIDs
        {
            public ApiClient AdminUiApiClient { get; set; }
            public ApiClient BuyerUiApiClient { get; set; }
            public ApiClient BuyerLocalUiApiClient { get; set; }
            public ApiClient MiddlewareApiClient { get; set; }
        }

        private async Task CreateApiClients(string token)
        {
            var allowedChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var integrationsClient = new ApiClient()
            {
                AppName = _integrationsApiClientName,
                Active = true,
                AllowAnyBuyer = false,
                AllowAnySupplier = false,
                AllowSeller = true,
                AccessTokenDuration = 600,
                RefreshTokenDuration = 43200,
                DefaultContextUserName = _sellerUserName,
                ClientSecret = RandomGen.GetString(allowedChars, 60)
            };
            var sellerClient = new ApiClient()
            {
                AppName = _sellerApiClientName,
                Active = true,
                AllowAnyBuyer = false,
                AllowAnySupplier = true,
                AllowSeller = true,
                AccessTokenDuration = 600,
                RefreshTokenDuration = 43200
            };
            var buyerClient = new ApiClient()
            {
                AppName = _buyerApiClientName,
                Active = true,
                AllowAnyBuyer = true,
                AllowAnySupplier = false,
                AllowSeller = false,
                AccessTokenDuration = 600,
                RefreshTokenDuration = 43200
            };
            var buyerLocalClient = new ApiClient()
            {
                AppName = _buyerLocalApiClientName,
                Active = true,
                AllowAnyBuyer = true,
                AllowAnySupplier = false,
                AllowSeller = false,
                AccessTokenDuration = 600,
                RefreshTokenDuration = 43200
            };

            var existingClients = await _oc.ApiClients.ListAllAsync(accessToken: token);

            var integrationsClientRequest = GetClientRequest(existingClients, integrationsClient, token);
            var sellerClientRequest = GetClientRequest(existingClients, sellerClient, token);
            var buyerClientRequest = GetClientRequest(existingClients, buyerClient, token);
            var buyerLocalClientRequest = GetClientRequest(existingClients, buyerLocalClient, token);

            await Task.WhenAll(integrationsClientRequest, sellerClientRequest, buyerClientRequest, buyerLocalClientRequest);
        }

        private Task<ApiClient> GetClientRequest(List<ApiClient> existingClients, ApiClient client, string token)
        {
            var match = existingClients.Find(c => c.AppName == client.AppName);
            return match != null ? _oc.ApiClients.SaveAsync(match.ID, client, token) : _oc.ApiClients.CreateAsync(client, token);
        }

        private async Task CreateMessageSenders(EnvironmentSeed seed, string accessToken)
        {
            foreach (var sender in DefaultMessageSenders())
            {
                var messageSender = await _oc.MessageSenders.SaveAsync(sender.ID, sender, accessToken);
                if (messageSender.ID == "BuyerEmails")
                {
                    foreach (var buyer in seed.Buyers)
                    {
                        await _oc.MessageSenders.SaveAssignmentAsync(new MessageSenderAssignment
                        {
                            MessageSenderID = messageSender.ID,
                            BuyerID = buyer.ID
                        }, accessToken);
                    }
                }
                else if (messageSender.ID == "SellerEmails")
                {
                    await _oc.MessageSenders.SaveAssignmentAsync(new MessageSenderAssignment
                    {
                        MessageSenderID = messageSender.ID
                    }, accessToken);
                }
                else if (messageSender.ID == "SupplierEmails")
                {
                    foreach (var supplier in seed.Suppliers)
                    {
                        await _oc.MessageSenders.SaveAssignmentAsync(new MessageSenderAssignment
                        {
                            MessageSenderID = messageSender.ID,
                            SupplierID = supplier.ID
                        }, accessToken);
                    }
                }
            }
        }

        private async Task CreateAndAssignIntegrationEvents(string[] buyerClientIDs, string localBuyerClientID, string token)
        {
            var checkoutEvent = new IntegrationEvent()
            {
                ElevatedRoles = new[] { ApiRole.FullAccess },
                ID = "HeadStartCheckout",
                EventType = IntegrationEventType.OrderCheckout,
                Name = "HeadStart Checkout",
                CustomImplementationUrl = _settings.EnvironmentSettings.MiddlewareBaseUrl,
                HashKey = _settings.OrderCloudSettings.WebhookHashKey,
                ConfigData = new
                {
                    ExcludePOProductsFromShipping = false,
                    ExcludePOProductsFromTax = true,
                }
            };
            await _oc.IntegrationEvents.SaveAsync(checkoutEvent.ID, checkoutEvent, token);

            var localCheckoutEvent = new IntegrationEvent()
            {
                ElevatedRoles = new[] { ApiRole.FullAccess },
                ID = "HeadStartCheckoutLOCAL",
                EventType = IntegrationEventType.OrderCheckout,
                CustomImplementationUrl = "https://marketplaceteam.ngrok.io", // local webhook url
                Name = "HeadStart Checkout LOCAL",
                HashKey = _settings.OrderCloudSettings.WebhookHashKey,
                ConfigData = new
                {
                    ExcludePOProductsFromShipping = false,
                    ExcludePOProductsFromTax = true,
                }
            };
            await _oc.IntegrationEvents.SaveAsync(localCheckoutEvent.ID, localCheckoutEvent, token);

            await _oc.ApiClients.PatchAsync(localBuyerClientID, new PartialApiClient { OrderCheckoutIntegrationEventID = "HeadStartCheckoutLOCAL" }, token);
            await Throttler.RunAsync(buyerClientIDs, 500, 20, clientID =>
                _oc.ApiClients.PatchAsync(clientID, new PartialApiClient { OrderCheckoutIntegrationEventID = "HeadStartCheckout" }, token));
        }

        private async Task ShutOffSupplierEmailsAsync(string token)
        {
            var allSuppliers = await _oc.Suppliers.ListAllAsync<HSSupplier>(accessToken: token);
            await Throttler.RunAsync(allSuppliers, 500, 20, supplier =>
                _oc.Suppliers.PatchAsync(supplier.ID, new PartialSupplier { xp = new { NotificationRcpts = new string[] { } } }, token));
        }

        public async Task CreateSecurityProfiles(string accessToken)
        {
            var profiles = DefaultSecurityProfiles.Select(p =>
                new SecurityProfile()
                {
                    Name = p.ID.ToString(),
                    ID = p.ID.ToString(),
                    CustomRoles = p.CustomRoles.Select(r => r.ToString()).ToList(),
                    Roles = p.Roles
                }).ToList();

            profiles.Add(new SecurityProfile()
            {
                Roles = new List<ApiRole> { ApiRole.FullAccess },
                Name = _fullAccessSecurityProfile,
                ID = _fullAccessSecurityProfile
            });

            var profileCreateRequests = profiles.Select(p => _oc.SecurityProfiles.SaveAsync(p.ID, p, accessToken));
            await Task.WhenAll(profileCreateRequests);
        }

        public async Task DeleteAllMessageSenders(string token)
        {
            var messageSenders = await _oc.MessageSenders.ListAllAsync(accessToken: token);
            await Throttler.RunAsync(messageSenders, 500, 20, messageSender =>
                _oc.MessageSenders.DeleteAsync(messageSender.ID, accessToken: token));
        }

        public async Task DeleteAllIntegrationEvents(string token)
        {
            // can't delete integration event if its referenced by an api client so first patch it to null
            var apiClientsWithIntegrationEvent = await _oc.ApiClients.ListAllAsync(filters: new { OrderCheckoutIntegrationEventID = "*" }, accessToken: token);
            await Throttler.RunAsync(apiClientsWithIntegrationEvent, 500, 20, apiClient =>
                _oc.ApiClients.PatchAsync(apiClient.ID, new PartialApiClient { OrderCheckoutIntegrationEventID = null }, accessToken: token));

            var integrationEvents = await _oc.IntegrationEvents.ListAllAsync(accessToken: token);
            await Throttler.RunAsync(integrationEvents, 500, 20, integrationEvent =>
                _oc.IntegrationEvents.DeleteAsync(integrationEvent.ID, accessToken: token));
        }

        private List<MessageSender> DefaultMessageSenders()
        {
            return new List<MessageSender>() {
                new MessageSender()
                {
                    ID = "BuyerEmails",
                    Name = "Buyer Emails",
                    MessageTypes = new[] {
                        MessageType.ForgottenPassword,
                        MessageType.NewUserInvitation,
                        MessageType.OrderApproved,
                        MessageType.OrderDeclined,
                        // MessageType.OrderSubmitted, this is currently being handled in PostOrderSubmitCommand, possibly move to message senders
                        MessageType.OrderSubmittedForApproval,
                        // MessageType.OrderSubmittedForYourApprovalHasBeenApproved, // too noisy
                        // MessageType.OrderSubmittedForYourApprovalHasBeenDeclined, // too noisy
                        // MessageType.ShipmentCreated this is currently being triggered in-app possibly move to message senders
                    },
                    URL = _settings.EnvironmentSettings.MiddlewareBaseUrl + "/messagesenders/{messagetype}",
                    SharedKey = _settings.OrderCloudSettings.WebhookHashKey
                },
                new MessageSender()
                {
                    ID = "SellerEmails",
                    Name = "Seller Emails",
                    MessageTypes = new[] {
                        MessageType.ForgottenPassword,
                    },
                    URL = _settings.EnvironmentSettings.MiddlewareBaseUrl + "/messagesenders/{messagetype}",
                    SharedKey = _settings.OrderCloudSettings.WebhookHashKey
                },
                new MessageSender()
                {
                    ID = "SupplierEmails",
                    Name = "Supplier Emails",
                    MessageTypes = new[] {
                        MessageType.ForgottenPassword,
                    },
                    URL = _settings.EnvironmentSettings.MiddlewareBaseUrl + "/messagesenders/{messagetype}",
                    SharedKey = _settings.OrderCloudSettings.WebhookHashKey
                }
            };
        }

        static readonly List<HSSecurityProfile> DefaultSecurityProfiles = new List<HSSecurityProfile>() {
			
			// seller/supplier
			new HSSecurityProfile() { ID = CustomRole.HSBuyerAdmin, CustomRoles = new CustomRole[] { CustomRole.HSBuyerAdmin }, Roles = new ApiRole[] { ApiRole.AddressAdmin, ApiRole.ApprovalRuleAdmin, ApiRole.BuyerAdmin, ApiRole.BuyerUserAdmin, ApiRole.CreditCardAdmin, ApiRole.UserGroupAdmin } },
            new HSSecurityProfile() { ID = CustomRole.HSBuyerImpersonator, CustomRoles = new CustomRole[] { CustomRole.HSBuyerImpersonator }, Roles = new ApiRole[] { ApiRole.BuyerImpersonation } },
            new HSSecurityProfile() { ID = CustomRole.HSCategoryAdmin, CustomRoles = new CustomRole[] { CustomRole.HSCategoryAdmin }, Roles = new ApiRole[] { ApiRole.CategoryAdmin } },
            new HSSecurityProfile() { ID = CustomRole.HSContentAdmin, CustomRoles = new CustomRole[] { CustomRole.AssetAdmin, CustomRole.DocumentAdmin, CustomRole.SchemaAdmin }, Roles = new ApiRole[] { ApiRole.ApiClientAdmin } },
            new HSSecurityProfile() { ID = CustomRole.HSMeAdmin, CustomRoles = new CustomRole[] { CustomRole.HSMeAdmin }, Roles = new ApiRole[] { ApiRole.MeAdmin, ApiRole.MeXpAdmin } },
            new HSSecurityProfile() { ID = CustomRole.HSMeProductAdmin, CustomRoles = new CustomRole[] { CustomRole.HSMeProductAdmin }, Roles = new ApiRole[] { ApiRole.InventoryAdmin, ApiRole.PriceScheduleAdmin, ApiRole.ProductAdmin, ApiRole.ProductFacetReader, ApiRole.SupplierAddressReader } },
            new HSSecurityProfile() { ID = CustomRole.HSMeSupplierAddressAdmin, CustomRoles = new CustomRole[] { CustomRole.HSMeSupplierAddressAdmin }, Roles = new ApiRole[] { ApiRole.SupplierAddressAdmin, ApiRole.SupplierReader } },
            new HSSecurityProfile() { ID = CustomRole.HSMeSupplierAdmin, CustomRoles = new CustomRole[] { CustomRole.AssetAdmin, CustomRole.HSMeSupplierAdmin }, Roles = new ApiRole[] { ApiRole.SupplierAdmin, ApiRole.SupplierReader } },
            new HSSecurityProfile() { ID = CustomRole.HSMeSupplierUserAdmin, CustomRoles = new CustomRole[] { CustomRole.HSMeSupplierUserAdmin }, Roles = new ApiRole[] { ApiRole.SupplierReader, ApiRole.SupplierUserAdmin } },
            new HSSecurityProfile() { ID = CustomRole.HSOrderAdmin, CustomRoles = new CustomRole[] { CustomRole.HSOrderAdmin }, Roles = new ApiRole[] { ApiRole.AddressReader, ApiRole.OrderAdmin, ApiRole.ShipmentReader } },
            new HSSecurityProfile() { ID = CustomRole.HSProductAdmin, CustomRoles = new CustomRole[] { CustomRole.HSProductAdmin }, Roles = new ApiRole[] { ApiRole.AdminAddressReader, ApiRole.CatalogAdmin, ApiRole.PriceScheduleAdmin, ApiRole.ProductAdmin, ApiRole.ProductAssignmentAdmin, ApiRole.ProductFacetAdmin, ApiRole.SupplierAddressReader } },
            new HSSecurityProfile() { ID = CustomRole.HSPromotionAdmin, CustomRoles = new CustomRole[] { CustomRole.HSPromotionAdmin }, Roles = new ApiRole[] { ApiRole.PromotionAdmin } },
            new HSSecurityProfile() { ID = CustomRole.HSReportAdmin, CustomRoles = new CustomRole[] { CustomRole.HSReportAdmin }, Roles = new ApiRole[] { } },
            new HSSecurityProfile() { ID = CustomRole.HSReportReader, CustomRoles = new CustomRole[] { CustomRole.HSReportReader }, Roles = new ApiRole[] { } },
            new HSSecurityProfile() { ID = CustomRole.HSSellerAdmin, CustomRoles = new CustomRole[] { CustomRole.HSSellerAdmin }, Roles = new ApiRole[] { ApiRole.AdminUserAdmin } },
            new HSSecurityProfile() { ID = CustomRole.HSShipmentAdmin, CustomRoles = new CustomRole[] { CustomRole.HSShipmentAdmin }, Roles = new ApiRole[] { ApiRole.AddressReader, ApiRole.OrderReader, ApiRole.ShipmentAdmin } },
            new HSSecurityProfile() { ID = CustomRole.HSStorefrontAdmin, CustomRoles = new CustomRole[] { CustomRole.HSStorefrontAdmin }, Roles = new ApiRole[] { ApiRole.ProductFacetAdmin, ApiRole.ProductFacetReader } },
            new HSSecurityProfile() { ID = CustomRole.HSSupplierAdmin, CustomRoles = new CustomRole[] { CustomRole.HSSupplierAdmin }, Roles = new ApiRole[] { ApiRole.SupplierAddressAdmin, ApiRole.SupplierAdmin, ApiRole.SupplierUserAdmin } },
            new HSSecurityProfile() { ID = CustomRole.HSSupplierUserGroupAdmin, CustomRoles = new CustomRole[] { CustomRole.HSSupplierUserGroupAdmin }, Roles = new ApiRole[] { ApiRole.SupplierReader, ApiRole.SupplierUserGroupAdmin } },
			
			// buyer - this is the only role needed for a buyer user to successfully check out
			new HSSecurityProfile() { ID = CustomRole.HSBaseBuyer, CustomRoles = new CustomRole[] { CustomRole.HSBaseBuyer }, Roles = new ApiRole[] { ApiRole.MeAddressAdmin, ApiRole.MeAdmin, ApiRole.MeCreditCardAdmin, ApiRole.MeXpAdmin, ApiRole.ProductFacetReader, ApiRole.Shopper, ApiRole.SupplierAddressReader, ApiRole.SupplierReader } },

			/* these roles don't do much, access to changing location information will be done through middleware calls that
			*  confirm the user is in the location specific access user group. These roles will be assigned to the location 
			*  specific user group and allow us to determine if a user has an admin role for at least one location through 
			*  the users JWT
			*/
			new HSSecurityProfile() { ID = CustomRole.HSLocationOrderApprover, CustomRoles = new CustomRole[] { CustomRole.HSLocationOrderApprover }, Roles = new ApiRole[] { } },
            new HSSecurityProfile() { ID = CustomRole.HSLocationViewAllOrders, CustomRoles = new CustomRole[] { CustomRole.HSLocationViewAllOrders }, Roles = new ApiRole[] { } },
            new HSSecurityProfile() { ID = CustomRole.HSLocationAddressAdmin, CustomRoles = new CustomRole[] { CustomRole.HSLocationAddressAdmin }, Roles = new ApiRole[] { } },
        };

        static readonly List<CustomRole> SellerHsRoles = new List<CustomRole>() {
            CustomRole.HSBuyerAdmin,
            CustomRole.HSBuyerImpersonator,
            CustomRole.HSCategoryAdmin,
            CustomRole.HSContentAdmin,
            CustomRole.HSMeAdmin,
            CustomRole.HSOrderAdmin,
            CustomRole.HSProductAdmin,
            CustomRole.HSPromotionAdmin,
            CustomRole.HSReportAdmin,
            CustomRole.HSReportReader,
            CustomRole.HSSellerAdmin,
            CustomRole.HSShipmentAdmin,
            CustomRole.HSStorefrontAdmin,
            CustomRole.HSSupplierAdmin,
            CustomRole.HSSupplierUserGroupAdmin
        };
    }
}
