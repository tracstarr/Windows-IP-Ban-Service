﻿#region Imports

using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Security.Permissions;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Web.Script.Serialization;
using System.Xml;
using System.Text.RegularExpressions;
using NLog;

#endregion Imports

namespace IPBan
{
    public class IPBanService : ServiceBase
    {
        private const int blockSize = 500;
        private const string scriptFileName = "banscript.txt";
        private const string fileScriptHeader = "pushd advfirewall firewall";
        private const string fileScriptAddLine = @"add rule name=""{0}"" remoteip=""{1}"" action=block protocol=any dir=in";
        private const string fileScriptDeleteLine = "delete rule name=\"{0}\"";
        private const string fileScriptEnd = "popd";

        private IPBanConfig config;
        private Thread serviceThread;
        private bool run;
        private EventLogQuery query;
        private EventLogWatcher watcher;
        private EventLogReader reader;
        private Dictionary<string, IPBlockCount> ipBlocker = new Dictionary<string, IPBlockCount>();
        private Dictionary<string, DateTime> ipBlockerDate = new Dictionary<string, DateTime>();
        private DateTime lastConfigFileDateTime = DateTime.MinValue;
        private readonly ManualResetEvent cycleEvent = new ManualResetEvent(false);
        private FileSystemWatcher fileWatcher;
        private FileStream file;
        private StreamReader streamReader;
        private long offset = 0;
        private long lastSize = 0;
        private List<string> usernames; 

        private void ExecuteBanScript()
        {
            lock (ipBlocker)
            {
                CreateRules();
                File.WriteAllLines(config.BanFile, ipBlocker.Keys.ToArray());
            }
        }

        private void ReadAppSettings()
        {
            try
            {
                string path = AppDomain.CurrentDomain.SetupInformation.ConfigurationFile;
                DateTime lastDateTime = File.GetLastWriteTimeUtc(path);
                if (lastDateTime > lastConfigFileDateTime)
                {
                    lastConfigFileDateTime = lastDateTime;
                    IPBanConfig newConfig = new IPBanConfig();
                    config = newConfig;
                }
            }
            catch (Exception ex)
            {
                Log.Write(LogLevel.Error, ex.ToString());

                if (config == null)
                {
                    throw new ApplicationException("Configuration failed to load, make sure to unblock all the files. Right click each file, select properties and then unblock.", ex);
                }
            }
        }

        private void LogInitialConfig()
        {
            Log.Write(LogLevel.Info, "Whitelist: {0}, Whitelist Regex: {1}", config.WhiteList, config.WhiteListRegex);
            Log.Write(LogLevel.Info, "Blacklist: {0}, Blacklist Regex: {1}", config.BlackList, config.BlackListRegex);

            if (!string.IsNullOrWhiteSpace(config.AllowedUserNames))
            {
                Log.Write(LogLevel.Info, "Allowed Users: {0}", config.AllowedUserNames);
            }
        }

        private string GetRuleName()
        {
            string ruleName = (config.RuleName ?? string.Empty).Trim();
            if (ruleName.Length == 0)
            {
                throw new ApplicationException("Failed to find RuleName in config file, cannot delete firewall rule");
            }

            return ruleName;
        }

        private void RunScript()
        {
            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "exec " + scriptFileName,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = true
            };
            Process.Start(info).WaitForExit();
        }

        private void DeleteRules(int ipAddressCount)
        {
            string ruleName = GetRuleName();
            string[] keys = ipBlockerDate.Keys.ToArray();
            string subRuleName;

            using (StreamWriter writer = File.CreateText(scriptFileName))
            {
                writer.WriteLine(fileScriptHeader);
                int i = 0;
                for (; i < ipAddressCount; i += blockSize)
                {
                    subRuleName = ruleName + i.ToString(CultureInfo.InvariantCulture);
                    writer.WriteLine(fileScriptDeleteLine, subRuleName);
                }
                // write out one last delete in case we dropped in ip address block count below a block size
                subRuleName = ruleName + i.ToString(CultureInfo.InvariantCulture);
                writer.WriteLine(fileScriptDeleteLine, subRuleName);
                writer.WriteLine(fileScriptEnd);
            }

            RunScript();
        }

        private void CreateRules()
        {
            string ruleName = GetRuleName();
            string[] keys = ipBlockerDate.Keys.ToArray();
            string subRuleName;

            using (StreamWriter writer = File.CreateText(scriptFileName))
            {
                writer.WriteLine(fileScriptHeader);
                int i = 0;
                for (; i < ipBlockerDate.Count; i += blockSize)
                {
                    subRuleName = ruleName + i.ToString(CultureInfo.InvariantCulture);
                    writer.WriteLine(fileScriptDeleteLine, subRuleName);
                    string ipAddresses = string.Join(",", keys.Skip(i).Take(blockSize));
                    string line = string.Format(fileScriptAddLine, subRuleName, ipAddresses);
                    writer.WriteLine(line);
                }

                // write out one last delete in case we dropped in ip address block count below a block size
                subRuleName = ruleName + i.ToString(CultureInfo.InvariantCulture);
                writer.WriteLine(fileScriptDeleteLine, subRuleName);
                writer.WriteLine(fileScriptEnd);
            }

            RunScript();
        }

        private void ProcessBanFileOnStart()
        {
            lock (ipBlocker)
            {
                ipBlocker.Clear();
                ipBlockerDate.Clear();

                if (File.Exists(config.BanFile))
                {
                    string[] lines = File.ReadAllLines(config.BanFile);

                    if (config.BanFileClearOnRestart)
                    {
                        DeleteRules(lines.Length);
                        File.Delete(config.BanFile);
                    }
                    else
                    {
                        IPAddress tmp;

                        foreach (string ip in lines)
                        {
                            string ipTrimmed = ip.Trim();
                            if (IPAddress.TryParse(ipTrimmed, out tmp))
                            {
                                IPBlockCount blockCount = new IPBlockCount();
                                blockCount.IncrementCount();
                                ipBlocker[ip] = blockCount;
                                ipBlockerDate[ip] = DateTime.UtcNow;
                            }
                        }
                    }
                }
            }
            ExecuteBanScript();
        }

        private XmlDocument ParseXml(string xml)
        {
            XmlTextReader reader = new XmlTextReader(new StringReader(xml));
            reader.Namespaces = false;
            XmlReader outerReader = XmlReader.Create(reader, new XmlReaderSettings { CheckCharacters = false });
            XmlDocument doc = new XmlDocument();
            doc.Load(outerReader);

            return doc;
        }

        private string ExtractIPAddressFromXml(XmlDocument doc)
        {
            string ipAddress = null;
            XmlNode keywordsNode = doc.SelectSingleNode("//Keywords");
            string keywordsText = keywordsNode.InnerText;
            if (keywordsText.StartsWith("0x"))
            {
                keywordsText = keywordsText.Substring(2);
            }
            ulong keywordsULONG = ulong.Parse(keywordsText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

            if (keywordsNode != null)
            {
                // we must match on keywords
                foreach (ExpressionsToBlockGroup group in config.GetGroupsMatchingKeywords(keywordsULONG))
                {
                    foreach (ExpressionToBlock expression in group.Expressions)
                    {
                        // we must find a node for each xpath expression
                        XmlNodeList nodes = doc.SelectNodes(expression.XPath);

                        if (nodes.Count == 0)
                        {
                            Log.Write(LogLevel.Warning, "No nodes found for xpath {0}", expression.XPath);
                            ipAddress = null;
                            break;
                        }

                        // if there is a regex, it must match
                        if (string.IsNullOrWhiteSpace(expression.Regex))
                        {
                            Log.Write(LogLevel.Info, "No regex, so counting as a match");
                        }
                        else
                        {
                            bool foundMatch = false;

                            foreach (XmlNode node in nodes)
                            {
                                Match m = expression.RegexObject.Match(node.InnerText);
                                if (m.Success)
                                {
                                    // check if the regex had an ipadddress group
                                    Group ipAddressGroup = m.Groups["ipaddress"];
                                    if (ipAddressGroup != null && ipAddressGroup.Success && !string.IsNullOrWhiteSpace(ipAddressGroup.Value))
                                    {
                                        string tempIPAddress = ipAddressGroup.Value.Trim();
                                        IPAddress tmp;
                                        if (IPAddress.TryParse(tempIPAddress, out tmp))
                                        {
                                            ipAddress = tempIPAddress;
                                            foundMatch = true;
                                            break;
                                        }
                                    }
                                    else
                                    {
                                        foundMatch = true;
                                        break;
                                    }
                                }
                            }

                            if (!foundMatch)
                            {
                                // no match, move on to the next group to check
                                Log.Write(LogLevel.Warning, "Regex {0} did not match any nodes with xpath {1}", expression.Regex, expression.XPath);
                                ipAddress = null;
                                break;
                            }
                        }
                    }

                    if (ipAddress != null)
                    {
                        break;
                    }
                }
            }

            return ipAddress;
        }

        private void ProcessIPAddress(string ipAddress, XmlDocument doc)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
            {
                return;
            }

            string userName = null;
            XmlNode userNameNode = doc.SelectSingleNode("//Data[@Name='TargetUserName']");
            if (userNameNode != null)
            {
                userName = userNameNode.InnerText.Trim();
            }

            if (config.IsWhiteListed(ipAddress))
            {
                Log.Write(LogLevel.Info, "Ignoring whitelisted ip address {0}, user name: {1}", ipAddress, userName);
            }
            else
            {
                lock (ipBlocker)
                {
                    // Get the IPBlockCount, if one exists.
                    IPBlockCount ipBlockCount;
                    ipBlocker.TryGetValue(ipAddress, out ipBlockCount);
                    if (ipBlockCount == null)
                    {
                        // This is the first failed login attempt, so record a new IPBlockCount.
                        ipBlockCount = new IPBlockCount();
                        ipBlocker[ipAddress] = ipBlockCount;
                    }

                    // Increment the count.
                    ipBlockCount.IncrementCount();

                    Log.Write(LogLevel.Info, "Incrementing count for ip {0} to {1}, user name: {2}", ipAddress, ipBlockCount.Count, userName);

                    // check for the target user name for additional blacklisting checks                    
                    bool blackListed = config.IsBlackListed(ipAddress) || (userName != null && config.IsBlackListed(userName));

                    // if the ip is black listed or they have reached the maximum failed login attempts before ban, ban them
                    if (blackListed || ipBlockCount.Count >= config.FailedLoginAttemptsBeforeBan)
                    {
                        // if they are not black listed OR this is the first increment of a black listed ip address, perform the ban
                        if (!blackListed || ipBlockCount.Count >= 1)
                        {
                            if (!ipBlockerDate.ContainsKey(ipAddress))
                            {
                                Log.Write(LogLevel.Error, "Banning ip address: {0}, user name: {1}, black listed: {2}, count: {3}", ipAddress, userName, blackListed, ipBlockCount.Count);
                                ipBlockerDate[ipAddress] = DateTime.UtcNow;
                                ExecuteBanScript();
                            }
                        }
                        else
                        {
                            Log.Write(LogLevel.Info, "Ignoring previously banned black listed ip {0}, user name: {1}, ip should already be banned", ipAddress, userName);
                        }
                    }
                    else if (ipBlockCount.Count > config.FailedLoginAttemptsBeforeBan)
                    {
                        Log.Write(LogLevel.Warning, "Got event with ip address {0}, count {1}, ip should already be banned", ipAddress, ipBlockCount.Count);
                    }
                }
            }
        }

        private void ProcessXml(string xml)
        {
            Log.Write(LogLevel.Info, "Processing xml: {0}", xml);

            XmlDocument doc = ParseXml(xml);
            string ipAddress = ExtractIPAddressFromXml(doc);
            ProcessIPAddress(ipAddress, doc);
        }

        private void EventRecordWritten(object sender, EventRecordWrittenEventArgs e)
        {
            try
            {
                EventRecord rec = e.EventRecord;
                string xml = rec.ToXml();
                ProcessXml(xml);
            }
            catch (Exception ex)
            {
                Log.Write(LogLevel.Error, ex.ToString());
            }
        }

        private string GetQueryString()
        {
            int id = 0;
            string queryString = "<QueryList>";
            foreach (ExpressionsToBlockGroup group in config.Expressions.Groups)
            {
                ulong keywordsDecimal = ulong.Parse(group.Keywords.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                queryString += "<Query Id='" + (++id).ToString() + "' Path='" + group.Path + "'><Select Path='" + group.Path + "'>*[System[(band(Keywords," + keywordsDecimal.ToString() + "))]]</Select></Query>";
            }
            queryString += "</QueryList>";

            return queryString;
        }

      
        private void SetupWatcher()
        {
            string queryString = GetQueryString();
            query = new EventLogQuery(null, PathType.LogName, queryString);
            reader = new EventLogReader(query);
            reader.BatchSize = 10;
            watcher = new EventLogWatcher(query);
            watcher.EventRecordWritten += EventRecordWritten;
            watcher.Enabled = true;

            if (!string.IsNullOrEmpty(config.WindSFTPLogFileName) && File.Exists(config.WindSFTPLogFileName) && File.Exists(config.WindSFTPUsersFileName))
            {

                GetUsersFromFile();
                file = new FileStream(config.WindSFTPLogFileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                streamReader = new StreamReader(file);
                lastSize = file.Length;
                ProcessCurrentLogFile();

                fileWatcher = new FileSystemWatcher
                {
                    Path = Path.GetDirectoryName(config.WindSFTPLogFileName),
                    Filter = Path.GetFileName(config.WindSFTPLogFileName),
                    NotifyFilter = NotifyFilters.LastWrite|NotifyFilters.Size
                };
                fileWatcher.Changed += FileWatcherOnChanged;
                fileWatcher.EnableRaisingEvents = true;
            }
            
        }

        private void GetUsersFromFile()
        {
            usernames = new List<string>();
            // Load the document and set the root element.
            XmlDocument doc = new XmlDocument();
            doc.Load(config.WindSFTPUsersFileName);
            XmlNode root = doc.DocumentElement;
            
            // Select all nodes where the book price is greater than 10.00.
            XmlNodeList nodeList = root.SelectNodes("//username");
            foreach (XmlNode user in nodeList)
            {
                usernames.Add(user.InnerText);
            }
        }

        private void ProcessCurrentLogFile()
        {
            // (?<=Denying Access to User: ).*?(?=\s)
            // \b(?:[0-9]{1,3}\.){3}[0-9]{1,3}\b
            
            file.Seek(offset, SeekOrigin.Begin);

            if (!streamReader.EndOfStream)
            {
                do
                {
                    string source = streamReader.ReadLine();
                    Regex re = new Regex(@"(?<=Denying Access to User: ).*?(?=\s)");
                    var mc = re.Matches(source);
                    if (mc.Count > 0)
                    {
                        string user = mc[0].Groups[0].Value;
                        string ip = string.Empty;

                        re = new Regex(@"\b(?:[0-9]{1,3}\.){3}[0-9]{1,3}\b");  // should be able to combine with above regex somehow, but i'm not good with regex
                        var ipm = re.Matches(source);
                        ip = ipm[0].Groups[0].Value;
                        ProcessIPAddress(ip);
                    }

                } while (!streamReader.EndOfStream);

                offset = file.Position;
            }
            
        }

        private void ProcessIPAddress(string ipAddress)
        {

            lock (ipBlocker)
            {
                // Get the IPBlockCount, if one exists.
                IPBlockCount ipBlockCount;
                ipBlocker.TryGetValue(ipAddress, out ipBlockCount);
                if (ipBlockCount == null)
                {
                    // This is the first failed login attempt, so record a new IPBlockCount.
                    ipBlockCount = new IPBlockCount();
                    ipBlocker[ipAddress] = ipBlockCount;
                }

                // Increment the count.
                ipBlockCount.IncrementCount();

                Log.Write(LogLevel.Info, "Incrementing count for ip {0} to {1}", ipAddress, ipBlockCount.Count);

                // check for the target user name for additional blacklisting checks                    
                bool blackListed = config.IsBlackListed(ipAddress) ;

                // if the ip is black listed or they have reached the maximum failed login attempts before ban, ban them
                if (blackListed || ipBlockCount.Count >= config.FailedLoginAttemptsBeforeBan)
                {
                    // if they are not black listed OR this is the first increment of a black listed ip address, perform the ban
                    if (!blackListed || ipBlockCount.Count >= 1)
                    {
                        if (!ipBlockerDate.ContainsKey(ipAddress))
                        {
                            Log.Write(LogLevel.Error, "Banning ip address: {0}, black listed: {1}, count: {2}", ipAddress, blackListed, ipBlockCount.Count);
                            ipBlockerDate[ipAddress] = DateTime.UtcNow;
                            ExecuteBanScript();
                        }
                    }
                    else
                    {
                        Log.Write(LogLevel.Info, "Ignoring previously banned black listed ip {0}, ip should already be banned", ipAddress);
                    }
                }
                else if (ipBlockCount.Count > config.FailedLoginAttemptsBeforeBan)
                {
                    Log.Write(LogLevel.Warning, "Got event with ip address {0}, count {1}, ip should already be banned", ipAddress, ipBlockCount.Count);
                }
            }
        }

        private void FileWatcherOnChanged(object sender, FileSystemEventArgs fileSystemEventArgs)
        {
            if (file.Length < lastSize)
            {
                Log.Write(LogLevel.Info, "Log file got smaller. Probably reset for the day.");
                lastSize = file.Length;
                offset = 0;
            }
            
            ProcessCurrentLogFile();
        }

        private void TestRemoteDesktopAttemptWithPAddress(string ipAddress, int count)
        {
            string xml = string.Format(@"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Security-Auditing' Guid='{{54849625-5478-4994-A5BA-3E3B0328C30D}}' /><EventID>4625</EventID><Version>0</Version><Level>0</Level><Task>12544</Task><Opcode>0</Opcode><Keywords>0x8010000000000000</Keywords><TimeCreated SystemTime='2012-03-25T17:12:36.848116500Z' /><EventRecordID>1657124</EventRecordID><Correlation /><Execution ProcessID='544' ThreadID='6616' /><Channel>Security</Channel><Computer>69-64-65-123</Computer><Security /></System><EventData><Data Name='SubjectUserSid'>S-1-5-18</Data><Data Name='SubjectUserName'>69-64-65-123$</Data><Data Name='SubjectDomainName'>WORKGROUP</Data><Data Name='SubjectLogonId'>0x3e7</Data><Data Name='TargetUserSid'>S-1-0-0</Data><Data Name='TargetUserName'>forex</Data><Data Name='TargetDomainName'>69-64-65-123</Data><Data Name='Status'>0xc000006d</Data><Data Name='FailureReason'>%%2313</Data><Data Name='SubStatus'>0xc0000064</Data><Data Name='LogonType'>10</Data><Data Name='LogonProcessName'>User32 </Data><Data Name='AuthenticationPackageName'>Negotiate</Data><Data Name='WorkstationName'>69-64-65-123</Data><Data Name='TransmittedServices'>-</Data><Data Name='LmPackageName'>-</Data><Data Name='KeyLength'>0</Data><Data Name='ProcessId'>0x2e40</Data><Data Name='ProcessName'>C:\Windows\System32\winlogon.exe</Data><Data Name='IpAddress'>{0}</Data><Data Name='IpPort'>52813</Data></EventData></Event>", ipAddress);

            while (count-- > 0)
            {
                ProcessXml(xml);
            }
        }

        private void RunTests()
        {
            string[] xmlTestStrings = new string[]
            {
                @"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='ASP.NET 2.0.50727.0'/><EventID Qualifiers='32768'>1309</EventID><Level>3</Level><Task>3</Task><Keywords>0x80000000000000</Keywords><TimeCreated SystemTime='2014-07-10T23:37:57.000Z'/><EventRecordID>196334166</EventRecordID><Channel>Application</Channel><Computer>SERVIDOR</Computer><Security/></System><EventData><Data>3005</Data><Data>Excepci?n no controlada.</Data><Data>11/07/2014 1:37:57</Data><Data>10/07/2014 23:37:57</Data><Data>2b4bdc4736fe40f9af42fce697b8acc7</Data><Data>9</Data><Data>7</Data><Data>0</Data><Data>/LM/W3SVC/44/ROOT-1-130495088933270000</Data><Data>Full</Data><Data>/</Data><Data>C:\Inetpub\vhosts\cbhermosilla.es\httpdocs\</Data><Data>SERVIDOR</Data><Data></Data><Data>116380</Data><Data>w3wp.exe</Data><Data>SERVIDOR\IWPD_36(cbhermosill)</Data><Data>HttpException</Data><Data>No se pueden validar datos. (le français [lə fʁɑ̃sɛ] ( listen) or la langue française [la lɑ̃ɡ fʁɑ̃sɛz]) 汉语 / 漢語 --:\x0013:--汉语 / 漢語</Data><Data>http://cbhermosilla.es/ScriptResource.axd?d=sdUSoDA_p4m7C8RvW7GhwLy4-JvXN1IcbzfRDWczGaZK4pT_avDiah8wSHZqBBjyvhhqa0cQYI_FWQYwCqlPsA8BsjFn19zRsw08qPt-rkQyZ6ODPVJ_Dp7CuLQKGPn6lQd-SOyyiu0VTTAgMiLVZqD6__M1&amp;t=635057131997880000</Data><Data>/ScriptResource.axd</Data><Data>66.249.76.207</Data><Data></Data><Data>False</Data><Data></Data><Data>SERVIDOR\IWPD_36(cbhermosill)</Data><Data>7</Data><Data>SERVIDOR\IWPD_36(cbhermosill)</Data><Data>False</Data><Data>   en System.Web.Configuration.MachineKeySection.EncryptOrDecryptData(Boolean fEncrypt, Byte[] buf, Byte[] modifier, Int32 start, Int32 length, IVType ivType, Boolean useValidationSymAlgo, Boolean signData) en System.Web.Configuration.MachineKeySection.EncryptOrDecryptData(Boolean fEncrypt, Byte[] buf, Byte[] modifier, Int32 start, Int32 length, IVType ivType, Boolean useValidationSymAlgo) en System.Web.UI.Page.DecryptStringWithIV(String s, IVType ivType) en System.Web.UI.Page.DecryptString(String s)</Data></EventData></Event>",
                @"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Security-Auditing' Guid='{54849625-5478-4994-A5BA-3E3B0328C30D}' /><EventID>4625</EventID><Version>0</Version><Level>0</Level><Task>12544</Task><Opcode>0</Opcode><Keywords>0x8010000000000000</Keywords><TimeCreated SystemTime='2012-03-25T17:12:36.848116500Z' /><EventRecordID>1657124</EventRecordID><Correlation /><Execution ProcessID='544' ThreadID='6616' /><Channel>Security</Channel><Computer>69-64-65-123</Computer><Security /></System><EventData><Data Name='SubjectUserSid'>S-1-5-18</Data><Data Name='SubjectUserName'>69-64-65-123$</Data><Data Name='SubjectDomainName'>WORKGROUP</Data><Data Name='SubjectLogonId'>0x3e7</Data><Data Name='TargetUserSid'>S-1-0-0</Data><Data Name='TargetUserName'>forex</Data><Data Name='TargetDomainName'>69-64-65-123</Data><Data Name='Status'>0xc000006d</Data><Data Name='FailureReason'>%%2313</Data><Data Name='SubStatus'>0xc0000064</Data><Data Name='LogonType'>10</Data><Data Name='LogonProcessName'>User32 </Data><Data Name='AuthenticationPackageName'>Negotiate</Data><Data Name='WorkstationName'>69-64-65-123</Data><Data Name='TransmittedServices'>-</Data><Data Name='LmPackageName'>-</Data><Data Name='KeyLength'>0</Data><Data Name='ProcessId'>0x2e40</Data><Data Name='ProcessName'>C:\Windows\System32\winlogon.exe</Data><Data Name='IpAddress'>99.99.99.99</Data><Data Name='IpPort'>52813</Data></EventData></Event>",
                @"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Security-Auditing' Guid='{54849625-5478-4994-A5BA-3E3B0328C30D}' /><EventID>4625</EventID><Version>0</Version><Level>0</Level><Task>12544</Task><Opcode>0</Opcode><Keywords>0x8010000000000000</Keywords><TimeCreated SystemTime='2012-03-25T17:12:36.848116500Z' /><EventRecordID>1657124</EventRecordID><Correlation /><Execution ProcessID='544' ThreadID='6616' /><Channel>Security</Channel><Computer>69-64-65-123</Computer><Security /></System><EventData><Data Name='SubjectUserSid'>S-1-5-18</Data><Data Name='SubjectUserName'>69-64-65-123$</Data><Data Name='SubjectDomainName'>WORKGROUP</Data><Data Name='SubjectLogonId'>0x3e7</Data><Data Name='TargetUserSid'>S-1-0-0</Data><Data Name='TargetUserName'>forex</Data><Data Name='TargetDomainName'>69-64-65-123</Data><Data Name='Status'>0xc000006d</Data><Data Name='FailureReason'>%%2313</Data><Data Name='SubStatus'>0xc0000064</Data><Data Name='LogonType'>10</Data><Data Name='LogonProcessName'>User32 </Data><Data Name='AuthenticationPackageName'>Negotiate</Data><Data Name='WorkstationName'>69-64-65-123</Data><Data Name='TransmittedServices'>-</Data><Data Name='LmPackageName'>-</Data><Data Name='KeyLength'>0</Data><Data Name='ProcessId'>0x2e40</Data><Data Name='ProcessName'>C:\Windows\System32\winlogon.exe</Data><Data Name='IpAddress'>127.0.0.1</Data><Data Name='IpPort'>52813</Data></EventData></Event>",
                @"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='MSSQLSERVER'/><EventID Qualifiers='49152'>18456</EventID><Level>0</Level><Task>4</Task><Keywords>0x90000000000000</Keywords><TimeCreated SystemTime='2012-04-05T20:26:30.000000000Z'/><EventRecordID>408488</EventRecordID><Channel>Application</Channel><Computer>dallas</Computer><Security/></System><EventData><Data>sa1</Data><Data> Reason: Could not find a login matching the name provided.</Data><Data> [CLIENT: 99.99.99.100]</Data><Binary>184800000E00000007000000440041004C004C00410053000000070000006D00610073007400650072000000</Binary></EventData></Event>",
                @"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='MSSQLSERVER'/><EventID Qualifiers='49152'>18456</EventID><Level>0</Level><Task>4</Task><Keywords>0x90000000000000</Keywords><TimeCreated SystemTime='2012-04-05T20:26:30.000000000Z'/><EventRecordID>408488</EventRecordID><Channel>Application</Channel><Computer>dallas</Computer><Security/></System><EventData><Data>sa1</Data><Data> Reason: Could not find a login matching the name provided.</Data><Data> [CLIENT: 0.0.0.0]</Data><Binary>184800000E00000007000000440041004C004C00410053000000070000006D00610073007400650072000000</Binary></EventData></Event>",
                @"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Security-Auditing' Guid='{54849625-5478-4994-A5BA-3E3B0328C30D}' /><EventID>4625</EventID><Version>0</Version><Level>0</Level><Task>12544</Task><Opcode>0</Opcode><Keywords>0x8010000000000000</Keywords><TimeCreated SystemTime='2012-03-25T17:12:36.848116500Z' /><EventRecordID>1657124</EventRecordID><Correlation /><Execution ProcessID='544' ThreadID='6616' /><Channel>Security</Channel><Computer>69-64-65-123</Computer><Security /></System><EventData><Data Name='SubjectUserSid'>S-1-5-18</Data><Data Name='SubjectUserName'>69-64-65-123$</Data><Data Name='SubjectDomainName'>WORKGROUP</Data><Data Name='SubjectLogonId'>0x3e7</Data><Data Name='TargetUserSid'>S-1-0-0</Data><Data Name='TargetUserName'>forex</Data><Data Name='TargetDomainName'>69-64-65-123</Data><Data Name='Status'>0xc000006d</Data><Data Name='FailureReason'>%%2313</Data><Data Name='SubStatus'>0xc0000064</Data><Data Name='LogonType'>10</Data><Data Name='LogonProcessName'>User32 </Data><Data Name='AuthenticationPackageName'>Negotiate</Data><Data Name='WorkstationName'>69-64-65-123</Data><Data Name='TransmittedServices'>-</Data><Data Name='LmPackageName'>-</Data><Data Name='KeyLength'>0</Data><Data Name='ProcessId'>0x2e40</Data><Data Name='ProcessName'>C:\Windows\System32\winlogon.exe</Data><Data Name='IpAddress'>99.99.99.98</Data><Data Name='IpPort'>52813</Data></EventData></Event>",
                @"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Security-Auditing' Guid='{54849625-5478-4994-A5BA-3E3B0328C30D}' /><EventID>5152</EventID><Version>0</Version><Level>0</Level><Task>12809</Task><Opcode>0</Opcode><Keywords>0x8010000000000000</Keywords><TimeCreated SystemTime='2013-07-23T22:33:04.141430800Z' /><EventRecordID>4892828</EventRecordID><Correlation /><Execution ProcessID='4' ThreadID='72' /><Channel>Security</Channel><Computer>HostWeb30.hostworx.co.za</Computer><Security /></System><EventData><Data Name='ProcessId'>0</Data><Data Name='Application'>-</Data><Data Name='Direction'>%%14592</Data><Data Name='SourceAddress'>37.140.141.29</Data><Data Name='SourcePort'>32480</Data><Data Name='DestAddress'>196.22.190.33</Data><Data Name='DestPort'>80</Data><Data Name='Protocol'>6</Data><Data Name='FilterRTID'>689661</Data><Data Name='LayerName'>%%14597</Data><Data Name='LayerRTID'>13</Data></EventData></Event>",
                @"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Security-Auditing' Guid='{54849625-5478-4994-A5BA-3E3B0328C30D}'/><EventID>5152</EventID><Version>0</Version><Level>0</Level><Task>12809</Task><Opcode>0</Opcode><Keywords>0x8010000000000000</Keywords><TimeCreated SystemTime='2013-07-24T11:09:21.153847400Z'/><EventRecordID>4910290</EventRecordID><Correlation/><Execution ProcessID='4' ThreadID='76'/><Channel>Security</Channel><Computer>HostWeb30.hostworx.co.za</Computer><Security/></System><EventData><Data Name='ProcessId'>4</Data><Data Name='Application'>System</Data><Data Name='Direction'>%%14592</Data><Data Name='SourceAddress'>82.61.45.195</Data><Data Name='SourcePort'>3079</Data><Data Name='DestAddress'>196.22.190.31</Data><Data Name='DestPort'>445</Data><Data Name='Protocol'>6</Data><Data Name='FilterRTID'>755725</Data><Data Name='LayerName'>%%14610</Data><Data Name='LayerRTID'>44</Data></EventData></Event>",
                @"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Security-Auditing' Guid='{54849625-5478-4994-A5BA-3E3B0328C30D}'/><EventID>4625</EventID><Version>0</Version><Level>0</Level><Task>12809</Task><Opcode>0</Opcode><Keywords>0x8010000000000001</Keywords><TimeCreated SystemTime='2013-07-24T11:24:51.369052700Z'/><EventRecordID>4910770</EventRecordID><Correlation/><Execution ProcessID='4' ThreadID='88'/><Channel>Security</Channel><Computer>HostWeb30.hostworx.co.za</Computer><Security/></System><EventData><Data Name='ProcessId'>2788</Data><Data Name='Application'>\device\harddiskvolume2\program files (x86)\rhinosoft.com\serv-u\servudaemon.exe</Data><Data Name='Direction'>%%14592</Data><Data Name='SourceAddress'>37.235.53.240</Data><Data Name='SourcePort'>39058</Data><Data Name='DestAddress'>196.22.190.31</Data><Data Name='DestPort'>21</Data><Data Name='Protocol'>6</Data><Data Name='FilterRTID'>780480</Data><Data Name='LayerName'>%%14610</Data><Data Name='LayerRTID'>44</Data></EventData></Event>",
                @"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='MSSQLSERVER'/><EventID Qualifiers='49152'>18456</EventID><Level>0</Level><Task>4</Task><Keywords>0x90000000000000</Keywords><TimeCreated SystemTime='2014-08-25T09:11:06.000000000Z'/><EventRecordID>116411121</EventRecordID><Channel>Application</Channel><Computer>s16240956</Computer><Security/></System><EventData><Data>sa</Data><Data> Raison : impossible de trouver une connexion correspondant au nom fourni.</Data><Data> [CLIENT : 218.10.17.192]</Data><Binary>184800000E0000000A0000005300310036003200340030003900350036000000070000006D00610073007400650072000000</Binary></EventData></Event>",
                @"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='MSExchangeTransport' /><EventID Qualifiers='32772'>1035</EventID><Level>3</Level><Task>1</Task><Keywords>0x80000000000000</Keywords><TimeCreated SystemTime='2015-06-08T08:13:12.000000000Z' /><EventRecordID>667364</EventRecordID><Channel>Application</Channel><Computer>DC.sicoir.local</Computer><Security /></System><EventData><Data>LogonDenied</Data><Data>Default DC</Data><Data>Ntlm</Data><Data>212.48.88.133</Data></EventData></Event>",
                @"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='MSSQLSERVER' /><EventID Qualifiers='49152'>18456</EventID><Level>0</Level><Task>4</Task><Keywords>0x90000000000000</Keywords><TimeCreated SystemTime='2015-09-10T14:20:42.000000000Z' /><EventRecordID>4439286</EventRecordID><Channel>Application</Channel><Computer>DSVR018379</Computer><Security /></System><EventData><Data>sa</Data><Data>Reason: Password did not match that for the login provided.</Data><Data>[CLIENT: 222.186.61.16]</Data><Binary>184800000E0000000B00000044005300560052003000310038003300370039000000070000006D00610073007400650072000000</Binary></EventData></Event>"
            };

            string[] xmlTestStringsDelay = new string[]
            {
                xmlTestStrings[5]
            };

            foreach (string xml in xmlTestStrings)
            {
                ProcessXml(xml);
            }

            TestRemoteDesktopAttemptWithPAddress("99.99.99.98", 10);

            foreach (string xml in xmlTestStringsDelay)
            {
                // Fire this test event after a 15 second delay, to test ExpireTime duration.
                ThreadPool.QueueUserWorkItem(new WaitCallback(DelayTest), xml);
            }
        }

        private void DelayTest(object stateInfo)
        {
            Thread.Sleep(15000);
            ProcessXml((string)stateInfo);
        }

        private void Initialize()
        {
            ReadAppSettings();
            ProcessBanFileOnStart();

#if DEBUG

            RunTests();

#endif

            SetupWatcher();
            LogInitialConfig();

        }

        private void CheckForExpiredIP()
        {
            List<string> ipAddressesToForget = new List<string>();
            bool fileChanged = false;
            KeyValuePair<string, DateTime>[] blockList;
            KeyValuePair<string, IPBlockCount>[] ipBlockCountList;

            // brief lock, we make copies of everything and work on the copies so we don't hold a lock too long
            lock (ipBlocker)
            {
                blockList = ipBlockerDate.ToArray();
                ipBlockCountList = ipBlocker.ToArray();
            }

            DateTime now = DateTime.UtcNow;

            // Check the block list for expired IPs.
            foreach (KeyValuePair<string, DateTime> keyValue in blockList)
            {
                // never un-ban a blacklisted entry
                if (config.IsBlackListed(keyValue.Key))
                {
                    continue;
                }

                TimeSpan elapsed = now - keyValue.Value;
                if (elapsed > config.BanTime)
                {
                    Log.Write(LogLevel.Error, "Un-banning ip address {0}", keyValue.Key);
                    lock (ipBlocker)
                    {
                        // take the ip out of the lists and mark the file as changed so that the ban script re-runs without this ip
                        ipBlockerDate.Remove(keyValue.Key);
                        ipBlocker.Remove(keyValue.Key);
                        fileChanged = true;
                    }
                }
            }

            // if we are allowing ip addresses failed login attempts to expire and get reset back to 0
            if (config.ExpireTime.TotalSeconds > 0)
            {
                // Check the list of failed login attempts, that are not yet blocked, for expired IPs.
                foreach (KeyValuePair<string, IPBlockCount> keyValue in ipBlockCountList)
                {
                    if (config.IsBlackListed(keyValue.Key))
                    {
                        continue;
                    }

                    // Find this IP address in the block list.
                    var block = from b in blockList
                                where b.Key == keyValue.Key
                                select b;

                    // If this IP is not yet blocked, and an invalid login attempt has not been made in the past timespan, see if we should forget it.
                    if (block.Count() == 0)
                    {
                        TimeSpan elapsed = (now - keyValue.Value.LastFailedLogin);

                        if (elapsed > config.ExpireTime)
                        {
                            Log.Write(LogLevel.Info, "Forgetting ip address {0}", keyValue.Key);
                            ipAddressesToForget.Add(keyValue.Key);
                        }
                    }
                }

                // Remove the IPs that have expired.
                lock (ipBlocker)
                {
                    foreach (string ip in ipAddressesToForget)
                    {
                        // no need to mark the file as changed because this ip was not banned, it only had some number of failed login attempts
                        ipBlocker.Remove(ip);
                    }
                }
            }

            // if the file changed, re-run the ban script with the updated list of ip addresses
            if (fileChanged)
            {
                ExecuteBanScript();
            }
        }

        private void ServiceThread()
        {
            Initialize();
            while (run)
            {
                CheckForExpiredIP();
                ReadAppSettings();
                cycleEvent.WaitOne(config.CycleTime);
            }
        }

        protected override void OnStart(string[] args)
        {
            base.OnStart(args);

            Log.Write(LogLevel.Info, "Started IPBan service");
            run = true;
            serviceThread = new Thread(new ThreadStart(ServiceThread));
            serviceThread.Start();
        }

        protected override void OnStop()
        {
            base.OnStop();

            run = false;
            query = null;
            watcher = null;

            Log.Write(LogLevel.Info, "Stopped IPBan service");
        }

        public IPBanService()
        {
            OperatingSystem os = Environment.OSVersion;
            Version vs = os.Version;
        }

        public static void RunService(string[] args)
        {
            System.ServiceProcess.ServiceBase[] ServicesToRun;
            ServicesToRun = new System.ServiceProcess.ServiceBase[] { new IPBanService() };
            System.ServiceProcess.ServiceBase.Run(ServicesToRun);
        }

        public static void RunConsole(string[] args)
        {
            IPBanService svc = new IPBanService();
            svc.OnStart(args);
            Console.WriteLine("Press ENTER to quit");
            string line;
            while ((line = Console.ReadLine()).Length != 0)
            {
                if (line.Equals("t", StringComparison.OrdinalIgnoreCase))
                {
                    svc.ReadAppSettings();
                    svc.RunTests();
                }
            }
            svc.OnStop();
            svc.cycleEvent.Set();
        }

        public static void Main(string[] args)
        {
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

            if (args.Length != 0 && args[0] == "debug")
            {
                RunConsole(args);
            }
            else
            {
                RunService(args);
            }
        }
    }
}


/*
<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='ASP.NET 2.0.50727.0'/><EventID Qualifiers='32768'>1309</EventID><Level>3</Level><Task>3</Task><Keywords>0x80000000000000</Keywords><TimeCreated SystemTime='2014-07-10T23:37:57.000Z'/><EventRecordID>196334166</EventRecordID><Channel>Application</Channel><Computer>SERVIDOR</Computer><Security/></System><EventData><Data>3005</Data><Data>Excepci?n no controlada.</Data><Data>11/07/2014 1:37:57</Data><Data>10/07/2014 23:37:57</Data><Data>2b4bdc4736fe40f9af42fce697b8acc7</Data><Data>9</Data><Data>7</Data><Data>0</Data><Data>/LM/W3SVC/44/ROOT-1-130495088933270000</Data><Data>Full</Data><Data>/</Data><Data>C:\Inetpub\vhosts\cbhermosilla.es\httpdocs\</Data><Data>SERVIDOR</Data><Data></Data><Data>116380</Data><Data>w3wp.exe</Data><Data>SERVIDOR\IWPD_36(cbhermosill)</Data><Data>HttpException</Data><Data>No se pueden validar datos.</Data><Data>http://cbhermosilla.es/ScriptResource.axd?d=sdUSoDA_p4m7C8RvW7GhwLy4-JvXN1IcbzfRDWczGaZK4pT_avDiah8wSHZqBBjyvhhqa0cQYI_FWQYwCqlPsA8BsjFn19zRsw08qPt-rkQyZ6ODPVJ_Dp7CuLQKGPn6lQd-SOyyiu0VTTAgMiLVZqD6__M1&amp;t=635057131997880000</Data><Data>/ScriptResource.axd</Data><Data>66.249.76.207</Data><Data></Data><Data>False</Data><Data></Data><Data>SERVIDOR\IWPD_36(cbhermosill)</Data><Data>7</Data><Data>SERVIDOR\IWPD_36(cbhermosill)</Data><Data>False</Data><Data>   en System.Web.Configuration.MachineKeySection.EncryptOrDecryptData(Boolean fEncrypt, Byte[] buf, Byte[] modifier, Int32 start, Int32 length, IVType ivType, Boolean useValidationSymAlgo, Boolean signData)
  en System.Web.Configuration.MachineKeySection.EncryptOrDecryptData(Boolean fEncrypt, Byte[] buf, Byte[] modifier, Int32 start, Int32 length, IVType ivType, Boolean useValidationSymAlgo)
  en System.Web.UI.Page.DecryptStringWithIV(String s, IVType ivType)
  en System.Web.UI.Page.DecryptString(String s)
</Data></EventData></Event>
<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Security-Auditing' Guid='{54849625-5478-4994-A5BA-3E3B0328C30D}'/><EventID>4625</EventID><Version>0</Version><Level>0</Level><Task>12544</Task><Opcode>0</Opcode><Keywords>0x8010000000000000</Keywords><TimeCreated SystemTime='2012-02-19T05:10:05.080038000Z'/><EventRecordID>1633642</EventRecordID><Correlation/><Execution ProcessID='544' ThreadID='4472'/><Channel>Security</Channel><Computer>69-64-65-123</Computer><Security/></System><EventData><Data Name='SubjectUserSid'>S-1-5-18</Data><Data Name='SubjectUserName'>69-64-65-123$</Data><Data Name='SubjectDomainName'>WORKGROUP</Data><Data Name='SubjectLogonId'>0x3e7</Data><Data Name='TargetUserSid'>S-1-0-0</Data><Data Name='TargetUserName'>user</Data><Data Name='TargetDomainName'>69-64-65-123</Data><Data Name='Status'>0xc000006d</Data><Data Name='FailureReason'>%%2313</Data><Data Name='SubStatus'>0xc0000064</Data><Data Name='LogonType'>10</Data><Data Name='LogonProcessName'>User32 </Data><Data Name='AuthenticationPackageName'>Negotiate</Data><Data Name='WorkstationName'>69-64-65-123</Data><Data Name='TransmittedServices'>-</Data><Data Name='LmPackageName'>-</Data><Data Name='KeyLength'>0</Data><Data Name='ProcessId'>0x1959c</Data><Data Name='ProcessName'>C:\Windows\System32\winlogon.exe</Data><Data Name='IpAddress'>183.62.15.154</Data><Data Name='IpPort'>22272</Data></EventData></Event>

*/