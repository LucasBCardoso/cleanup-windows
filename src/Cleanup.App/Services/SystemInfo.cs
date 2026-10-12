using Microsoft.Win32;

namespace Cleanup.App.Services;

public static class SystemInfo
{
    /// <summary>Controlled Folder Access can make Windows refuse moves out of protected folders; we report it, never bypass it.</summary>
    public static string ControlledFolderAccessText()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access");
            var on = key?.GetValue("EnableControlledFolderAccess") is int v && v == 1;
            return on
                ? "Ativado. O Windows pode bloquear o Cleanup em Documentos, Imagens e outras pastas protegidas; se isso acontecer, permita o Cleanup na Segurança do Windows."
                : "Desativado. O Cleanup pode mover itens das suas pastas para a Lixeira normalmente.";
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return "Não foi possível ler o estado. Se o Windows bloquear o Cleanup, permita-o na Segurança do Windows.";
        }
    }
}
