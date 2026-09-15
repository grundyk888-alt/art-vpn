using System.Security.Cryptography;
using System.Text;

namespace ArtSport.ArtVpn.Service;

internal static class ProviderReplacementTests
{
    public static async Task<object> RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"art-vpn-provider-replace-test-"+Guid.NewGuid().ToString("N"));
        var options=RuntimeOptions.Test(root,"ARTSPORT.ProviderReplaceTest."+Guid.NewGuid().ToString("N"));
        var first=Encoding.UTF8.GetBytes("https://first.example.invalid/fixture-only");
        var second=Encoding.UTF8.GetBytes("https://second.example.invalid/fixture-only");
        var checks=0;
        void Check(bool ok){if(!ok)throw new InvalidOperationException("ProviderReplacementRegression:"+checks);checks++;}
        bool Matches(byte[] expected) { var value=ProviderSecretStore.Open(options);try{return CryptographicOperations.FixedTimeEquals(value,expected);}finally{CryptographicOperations.ZeroMemory(value);} }
        try
        {
            ProviderSecretStore.StoreOrReplace(options,first,false);
            Check(Matches(first));
            var entropy=File.ReadAllBytes(options.ProviderEntropyPath);
            try
            {
                ProviderSecretStore.StoreOrReplace(options,first,false);
                Check(!File.Exists(options.ProviderSecretPath+".previous"));
                var invalid=false;
                try{ProviderSecretStore.StoreOrReplace(options,Encoding.UTF8.GetBytes("http://invalid.example/"),false);}catch(InvalidDataException){invalid=true;}
                Check(invalid && Matches(first));
                using(var held=new FileStream(options.ProviderSecretPath,FileMode.Open,FileAccess.Read,FileShare.Read))
                {
                    var blocked=false;
                    try{ProviderSecretStore.StoreOrReplace(options,second,false);}catch(IOException){blocked=true;}
                    Check(blocked && Matches(first));
                }
                ProviderSecretStore.StoreOrReplace(options,second,false);
                Check(Matches(second));
                Check(File.ReadAllBytes(options.ProviderEntropyPath).SequenceEqual(entropy));
                var backup=ProtectedData.Unprotect(File.ReadAllBytes(options.ProviderSecretPath+".previous"),entropy,DataProtectionScope.LocalMachine);
                try{Check(backup.SequenceEqual(first));}finally{CryptographicOperations.ZeroMemory(backup);}
                File.Delete(options.ProviderReceiptPath);Directory.CreateDirectory(options.ProviderReceiptPath);
                ProviderSecretStore.StoreOrReplace(options,first,false);
                Check(Matches(first)); // optional receipt failure cannot undo committed secret
                Directory.Delete(options.ProviderReceiptPath);
                var readers=Task.Run(()=>{for(var i=0;i<40;i++){var value=ProviderSecretStore.Open(options);try{if(!value.SequenceEqual(first)&&!value.SequenceEqual(second))throw new InvalidOperationException("TornProviderRead");}finally{CryptographicOperations.ZeroMemory(value);}}});
                for(var i=0;i<10;i++)ProviderSecretStore.StoreOrReplace(options,i%2==0?second:first,false);
                await readers;
                Check(Matches(first));
                Check(!Directory.EnumerateFiles(options.PrivateRoot,".provider-replacement-*.tmp").Any());
                foreach(var file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories))
                {
                    var bytes=File.ReadAllBytes(file);
                    try{Check(!Encoding.UTF8.GetString(bytes).Contains("example.invalid/fixture-only",StringComparison.Ordinal));}
                    finally{CryptographicOperations.ZeroMemory(bytes);}
                }
            }
            finally{CryptographicOperations.ZeroMemory(entropy);}
            return new {status="Passed",scenarios=checks,atomicEncryptedReplacement=true,previousSecretRetained=true,
                concurrentReadsVerified=true,productionNetworkChanged=false,secretDisplayed=false};
        }
        finally
        {
            CryptographicOperations.ZeroMemory(first);CryptographicOperations.ZeroMemory(second);
            var resolved=Path.GetFullPath(root);
            var boundary=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(resolved.StartsWith(boundary,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(resolved).StartsWith("art-vpn-provider-replace-test-",StringComparison.Ordinal)&&Directory.Exists(resolved))Directory.Delete(resolved,true);
        }
    }
}
