# Reset permissions and ownership in the repository to resolve SYSTEM lockouts
takeown /F "P:\Visual Studio\source\repos\WinAgent" /R /A /D Y
icacls "P:\Visual Studio\source\repos\WinAgent" /grant Administrators:`(OI`)`(CI`)F /T /C /Q
