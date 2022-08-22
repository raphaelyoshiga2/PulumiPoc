using Pulumi;
using Pulumi.AzureNative.Resources;
using Pulumi.AzureNative.Storage;
using System.Collections.Generic;
using System.Diagnostics;
using Pulumi.AzureNative.Authorization;
using Pulumi.AzureNative.Insights;
using Pulumi.AzureNative.KeyVault;
using Pulumi.AzureNative.KeyVault.Inputs;
using Pulumi.AzureNative.Web;
using Pulumi.AzureNative.Web.Inputs;
using Deployment = Pulumi.Deployment;
using Kind = Pulumi.AzureNative.Storage.Kind;
using SkuArgs = Pulumi.AzureNative.Storage.Inputs.SkuArgs;
using KeyVaultSkuArgs = Pulumi.AzureNative.KeyVault.Inputs.SkuArgs;
using SkuName = Pulumi.AzureNative.Storage.SkuName;
using Azure = Pulumi.AzureNative;
using ManagedServiceIdentityType = Pulumi.AzureNative.Web.ManagedServiceIdentityType;

static Output<string> SignedBlobReadUrl(Blob blob, BlobContainer container, StorageAccount account, ResourceGroup resourceGroup)
{
    var serviceSasToken = ListStorageAccountServiceSAS.Invoke(new ListStorageAccountServiceSASInvokeArgs
    {
        AccountName = account.Name,
        Protocols = HttpProtocol.Https,
        SharedAccessStartTime = "2021-01-01",
        SharedAccessExpiryTime = "2030-01-01",
        Resource = SignedResource.C,
        ResourceGroupName = resourceGroup.Name,
        Permissions = Permissions.R,
        CanonicalizedResource = Output.Format($"/blob/{account.Name}/{container.Name}"),
        ContentType = "application/json",
        CacheControl = "max-age=5",
        ContentDisposition = "inline",
        ContentEncoding = "deflate",
    }).Apply(blobSAS => blobSAS.ServiceSasToken);

    return Output.Format($"https://{account.Name}.blob.core.windows.net/{container.Name}/{blob.Name}?{serviceSasToken}");
}
static Output<string> GetConnectionString(Input<string> accountName,
    Output<string> storageKey)
{
    return Output.Format($"DefaultEndpointsProtocol=https;AccountName={accountName};AccountKey={storageKey}");
}

return await Pulumi.Deployment.RunAsync(() =>
{
    //Debugger.Launch();

    var stack = Deployment.Instance.StackName;
    // Create an Azure Resource Group
    var resourceGroup = new ResourceGroup($"contact-legacy-{stack}");
        //var clientConfig = Output.Create(GetClientConfig.InvokeAsync());
    var tenantId = "88a91815-758a-48c5-8810-c5520e8f581a";
    // Create an Azure resource (Storage Account)
    var storageAccount = new StorageAccount($"contactleg{stack}", new StorageAccountArgs
    {
        ResourceGroupName = resourceGroup.Name,
        Sku = new SkuArgs
        {
            Name = SkuName.Standard_LRS
        },
        Kind = Kind.StorageV2
    });

    var storageAccountKeys = ListStorageAccountKeys.Invoke(new ListStorageAccountKeysInvokeArgs
    {
        ResourceGroupName = resourceGroup.Name,
        AccountName = storageAccount.Name
    });

    var appInsights = new Component("appInsights", new ComponentArgs
    {
        ApplicationType = ApplicationType.Web,
        Kind = "web",
        ResourceGroupName = resourceGroup.Name,
    });

    var appServicePlan = new AppServicePlan($"contact-legacy-plan-{stack}", new AppServicePlanArgs
    {
        ResourceGroupName = resourceGroup.Name,
        Kind = "Linux",
        Sku = new SkuDescriptionArgs
        {
            Tier = "Dynamic",
            Name = "Y1"
        },
        Reserved = true
    });

    //var container = new BlobContainer("zips-container", new BlobContainerArgs
    //{
    //    AccountName = storageAccount.Name,
    //    PublicAccess = PublicAccess.None,
    //    ResourceGroupName = resourceGroup.Name,
    //});

    //var blob = new Blob("zip", new BlobArgs
    //{
    //    AccountName = storageAccount.Name,
    //    ContainerName = container.Name,
    //    ResourceGroupName = resourceGroup.Name,
    //    Type = BlobType.Block,
    //    Source = new FileArchive("./functions")
    //});

    //var codeBlobUrl = SignedBlobReadUrl(blob, container, storageAccount, resourceGroup);
    var primaryStorageKey = storageAccountKeys.Apply(accountKeys =>
    {
        var firstKey = accountKeys.Keys[0].Value;
        return Output.CreateSecret(firstKey);
    });

    // Export the primary key of the Storage Account
    new Dictionary<string, object?>
    {
        ["primaryStorageKey"] = primaryStorageKey
    };
    var siteConfigArgs = new SiteConfigArgs
    {
        AppSettings = new[]
        {
            new NameValuePairArgs{
                Name = "AzureWebJobsStorage",
                Value = GetConnectionString(storageAccount.Name, primaryStorageKey ),
            },
            new NameValuePairArgs{
                Name = "FUNCTIONS_WORKER_RUNTIME",
                Value = "dotnet",
            },
            new NameValuePairArgs{
                Name = "FUNCTIONS_EXTENSION_VERSION",
                Value = "~4",
            },
            new NameValuePairArgs{
                Name = "SCM_DO_BUILD_DURING_DEPLOYMENT",
                Value = $"FALSE",
            },
            new NameValuePairArgs{
                Name = "APPINSIGHTS_INSTRUMENTATIONKEY",
                Value = $"{appInsights.InstrumentationKey}",
            },
        },
    };
    var app = new WebApp($"contact-legacy-function-{stack}", new WebAppArgs
    {
        Name = $"contact-legacy-function-{stack}",
        Kind = "FunctionApp",
        ResourceGroupName = resourceGroup.Name,
        ServerFarmId = appServicePlan.Id,
        SiteConfig = siteConfigArgs,
        Identity = new ManagedServiceIdentityArgs()
        {
            Type = ManagedServiceIdentityType.SystemAssigned,
        }
    });

    var objectId = app.Identity.Apply(x => x.PrincipalId);

    var stagingSlot = new WebAppSlot("staging", new WebAppSlotArgs()
    {
        Name = app.Name,
        Kind = "FunctionApp",
        ResourceGroupName = resourceGroup.Name,
        ServerFarmId = appServicePlan.Id,
        SiteConfig = siteConfigArgs,
        Slot = "staging",
        Identity = new ManagedServiceIdentityArgs()
        {
            Type = ManagedServiceIdentityType.SystemAssigned,
        }
    });
    var vault = new Vault("vault", new()
    {
        VaultName = $"contact-legacy-vault-{stack}",
        Location = resourceGroup.Location,
        Properties = new VaultPropertiesArgs
        {
            AccessPolicies = new[]
            {
                new AccessPolicyEntryArgs
                {
                    ObjectId = objectId,
                    Permissions = new PermissionsArgs
                    {
                        Secrets =
                        {
                            SecretPermissions.Get
                        },
                    },
                    TenantId = tenantId,
                },
            },
            EnabledForDeployment = true,
            EnabledForDiskEncryption = true,
            EnabledForTemplateDeployment = true,
            Sku = new KeyVaultSkuArgs
            {
                Family = "A",
                Name = Pulumi.AzureNative.KeyVault.SkuName.Standard,
            },
            TenantId = tenantId,
        },
        ResourceGroupName = resourceGroup.Name

    });

});

