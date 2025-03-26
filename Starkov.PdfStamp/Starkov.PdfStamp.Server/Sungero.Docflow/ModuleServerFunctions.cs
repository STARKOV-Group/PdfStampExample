using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;
using Newtonsoft.Json;

namespace Starkov.PdfStamp.Module.Docflow.Server
{
  partial class ModuleFunctions
  {
    
    #region Расширенная штамповка.
    /// <summary>
    /// Отладка
    /// </summary>
    /// <param name="variables"></param>
    /// <param name="prefix"></param>
    [Public]
    public void DumpVariables(System.Collections.Generic.Dictionary<string, object> variables, string prefix = "")
    {      
      //DISCLAIMER: Просьба не считать то, что творится ниже - примером хорошего кода.
      foreach(var key in variables.Keys)
      {
        var value = variables[key];
        if (value == null || value is string)
        {
          Logger.DebugFormat("PdfStamp DumpVariables {0}.{1}={2}\r\n", prefix, key, value);
          continue;
        }
        
        if (value is Dictionary<string, object> || value is Dictionary<string, string> )
        {
          DumpVariables((value as Dictionary<string, object>), string.Format("{0}.{1}", prefix, key));
        } else if (value.GetType()?.Name.Contains("List") ?? false)
        {
          var arr = (List<Dictionary<string, string>>)value;
          for(int i=0; i < arr.Count; i++)
          {
            DumpVariables((arr[i] as Dictionary<string, string>), string.Format("{0}.{1}[{2}]", prefix, key, i));
          }
        }
      }
    }
    
    [Public]
    public void DumpVariables(System.Collections.Generic.Dictionary<string, string> variables, string prefix = "")
    {
      foreach(var key in variables.Keys)
      {
        var value = variables[key];
        Logger.DebugFormat("PdfStamp DumpVariables {0}.{1}={2}\r\n", prefix, key, value);
      }
    }

    
    /// <summary>
    /// Получить подписи для формирования штампа.
    /// </summary>
    /// <param name="versionId">Версия документа.</param>
    /// <param name="includeExternalSignature">Признак того, что в выборку включены внешние подписи.</param>
    /// <returns>Электронная подпись.</returns>
    [Public]
    public virtual List<Sungero.Domain.Shared.ISignature> GetSignaturesForMark(Sungero.Docflow.IOfficialDocument document, long versionId, bool includeExternalSignature)
    {
      var version = document.Versions.FirstOrDefault(x => x.Id == versionId);
      if (version == null)
        return null;
      
      var allSignatures = Signatures.Get(version, q => q.Where(s => includeExternalSignature || s.IsExternal != true))
        .ToList();
      
      if (!allSignatures.Any())
        return null;
      
      // В приоритете утверждающая подпись, отсортированная по дате с учетом замещения.
      return allSignatures
        .OrderBy(x => x.SignatureType)
        .ThenByDescending(s => s.SigningDate)
        .GroupBy(x => x.SubstitutedUser ?? x.Signatory)
        .Select(x => x.FirstOrDefault())
        .ToList();
    }

    /// <summary>
    /// Получить тело письма на основе шаблона и модели.
    /// </summary>
    /// <param name="template">Шаблон.</param>
    /// <param name="model">Модель.</param>
    /// <returns>Тело письма.</returns>
    [Public]
    public virtual string RenderTemplate(string template, System.Collections.Generic.Dictionary<string, object> model)
    {
      if (string.IsNullOrEmpty(template) || model == null)
        return string.Empty;
      
      return Nustache.Core.Render.StringToString(template, model,
                                                 new Nustache.Core.RenderContextBehaviour() {
                                                   OnException = ex => Logger.Error(ex.Message, ex)
                                                 }).Trim();
    }
    
    /// <summary>
    /// Получить отметку об ЭП для сертификата из подписи.
    /// </summary>
    /// <param name="signature">Подпись.</param>
    /// <param name="signatureStampParams">Параметры простановки отметки.</param>
    /// <returns>Изображение отметки об ЭП для сертификата в виде html.</returns>
    [Public]
    public virtual System.Collections.Generic.Dictionary<string, string> GetSignatureMarkForCertificateAsDict(Sungero.Domain.Shared.ISignature signature,
                                                                                                              Sungero.Docflow.IStampSetting stampSettings)
    {
      var result = new Dictionary<string, string>();
      if (signature == null)
        return result;
      
      var signatoryEmployee = Sungero.Company.Employees.As(signature.Signatory);
      var signatoryFullName = signatoryEmployee != null ? Sungero.Company.PublicFunctions.Employee.GetShortName(signatoryEmployee, false) : signature.SignatoryFullName;
      if (signature.SubstitutedUser != null)
        signatoryFullName += string.Format(" за {0}", Sungero.Company.Employees.Is(signature.SubstitutedUser) ?
                                           Sungero.Company.PublicFunctions.Employee.GetShortName(Sungero.Company.Employees.As(signature.SubstitutedUser), false) :
                                           signature.SubstitutedUserFullName);
      
      using (Sungero.Core.CultureInfoExtensions.SwitchTo(TenantInfo.Culture))
      {
        string validity =  string.Empty;
        
        var certificate = signature.SignCertificate;
        if (certificate != null)
        {
          var certificateSubject = this.GetCertificateSubject(signature);
          validity = string.Format("{0} {1} {2} {3}",
                                   Sungero.Company.Resources.From,
                                   certificate.NotBefore.Value.ToShortDateString(),
                                   Sungero.Company.Resources.To,
                                   certificate.NotAfter.Value.ToShortDateString());

          result["Thumbprint"] = certificate.Thumbprint.ToLower();
        }
        
        result["SignatoryFullName"] = signatoryFullName;
        result["UnifiedRegistrationNumber"] = this.GetUnsignedAttribute(signature, Sungero.Docflow.Constants.Module.UnsignedAdditionalInfoKeyFPoA);
        
        var utcOffset = Calendar.UtcOffset.TotalHours;
        var utcOffsetLabel = utcOffset >= 0 ? "+" + utcOffset.ToString() : utcOffset.ToString();
        result["SigningDateTimeUtc"] = Sungero.Docflow.PublicFunctions.Module.ToShortDateShortTime(signature.SigningDate.AddHours(utcOffset));
        result["SigningDateTime"] = signature.SigningDate.ToString();
        result["SigningDate"] = signature.SigningDate.ToShortDateString();
        
        result["Logo"] = string.Format("<img width=27 src=\"data:image/png;base64,{0}\"/>", stampSettings.LogoAsBase64);
        result["Title"] = Sungero.Docflow.PublicFunctions.StampSetting.GetTitleForSignatureStamp(stampSettings);

        result["Validity"] = validity;
      }



      
      return result;
    }
    
    /// <summary>
    /// Преобразовать в PDF с добавлением отметки.
    /// </summary>
    /// <param name="document">Документ для преобразования.</param>
    /// <param name="versionId">ИД версии.</param>
    /// <param name="htmlStamp">Отметка (html).</param>
    /// <param name="isSignatureMark">Признак отметки об ЭП. True - отметка об ЭП, False - отметка о поступлении.</param>
    /// <param name="rightIndent">Значение отступа справа (для отметки о поступлении).</param>
    /// <param name="bottomIndent">Значение отступа снизу (для отметки о поступлении).</param>
    /// <returns>Информация о результате преобразования в PDF с добавлением отметки.</returns>
    [Public]
    public virtual PdfStamp.Structures.Docflow.OfficialDocument.IConversionToPdfResult ConvertToPdfWithStampsCustom(Sungero.Docflow.IOfficialDocument document,
                                                                                                                    long versionId,
                                                                                                                    System.Collections.Generic.Dictionary<string, string> htmlStamps,
                                                                                                                    bool isSignatureMark)
    {
      // Предпроверки.
      var result = PdfStamp.Structures.Docflow.OfficialDocument.ConversionToPdfResult.Create();
      result.HasErrors = true;
      var version = document.Versions.SingleOrDefault(v => v.Id == versionId);
      if (version == null)
      {
        result.HasConvertionError = true;
        result.ErrorMessage = Sungero.Docflow.OfficialDocuments.Resources.NoVersionWithNumberErrorFormat(versionId);
        return result;
      }
      
      Sungero.Docflow.PublicFunctions.Module.LogPdfConverting("ConvertToPdfWithStampsCustom Start", document, version);
      
      // Получить тело версии для преобразования в PDF.
      var body = Sungero.Docflow.PublicFunctions.OfficialDocument.GetBodyToConvertToPdf(document, version, isSignatureMark);
      if (body == null || body.Body == null || body.Body.Length == 0)
      {
        Sungero.Docflow.PublicFunctions.Module.LogPdfConverting("Cannot get version body", document, version);
        result.HasConvertionError = true;
        result.ErrorMessage = isSignatureMark ? Sungero.Docflow.OfficialDocuments.Resources.ConvertionErrorTitleBase : Sungero.Docflow.Resources.AddRegistrationStampErrorTitle;
        return result;
      }
      
      System.IO.Stream pdfDocumentStream = null;
      using (var inputStream = new System.IO.MemoryStream(body.Body))
      {
        try
        {
          pdfDocumentStream = Sungero.Docflow.IsolatedFunctions.PdfConverter.GeneratePdf(inputStream, body.Extension);
          
          foreach(var anchor in htmlStamps.Keys)
          {
            var htmlStamp = htmlStamps[anchor];
            Logger.DebugFormat("ConvertToPdfWithStampsCustom {0}\n{1}", anchor, htmlStamp);
            
            // пропускаем пустые штампы
            if (string.IsNullOrEmpty(htmlStamp))
              continue;
            
            pdfDocumentStream = Sungero.Docflow.IsolatedFunctions.PdfConverter.AddSignatureStamp(pdfDocumentStream, body.Extension, htmlStamp, anchor, Sungero.Docflow.Constants.Module.SearchablePagesLimit);
          }
        }
        catch (Exception ex)
        {
          if (ex is AppliedCodeException)
            Logger.Error(Sungero.Docflow.Resources.PdfConvertErrorFormat(document.Id), ex.InnerException);
          else
            Logger.Error(Sungero.Docflow.Resources.PdfConvertErrorFormat(document.Id), ex);
          
          result.HasConvertionError = true;
          result.HasLockError = false;
          result.ErrorMessage = isSignatureMark ? Sungero.Docflow.OfficialDocuments.Resources.ConvertionErrorTitleBase : Sungero.Docflow.Resources.AddRegistrationStampErrorTitle;
        }
      }
      
      if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        return result;
      
      // Выключить error-логирование при доступе к зашифрованным бинарным данным/версии.
      AccessRights.SuppressSecurityEvents(
        () =>
        {
          if (isSignatureMark)
          {
            version.PublicBody.Write(pdfDocumentStream);
            version.AssociatedApplication = Sungero.Content.AssociatedApplications.GetByExtension(Sungero.Docflow.Constants.OfficialDocument.Extensions.Pdf);
          }
          else
          {
            document.CreateVersionFrom(pdfDocumentStream, Sungero.Docflow.Constants.OfficialDocument.Extensions.Pdf);
            
            var lastVersion = document.LastVersion;
            lastVersion.Note = Sungero.Docflow.OfficialDocuments.Resources.VersionWithRegistrationStamp;
          }
        });
      
      pdfDocumentStream.Close();
      
      this.LogPdfConverting(isSignatureMark ? "Generate public body" : "Create new version", document, version);
      
      var baseStructure = this.SaveDocumentAfterConvertToPdf(document, isSignatureMark);
      result.ErrorMessage = baseStructure.ErrorMessage;
      result.ErrorTitle = baseStructure.ErrorTitle;
      result.HasConvertionError = baseStructure.HasConvertionError;
      result.HasErrors = baseStructure.HasErrors;
      result.HasLockError = baseStructure.HasLockError;
      result.IsFastConvertion = baseStructure.IsFastConvertion;
      result.IsOnConvertion = baseStructure.IsFastConvertion;
      
      this.LogPdfConverting("ConvertToPdfWithStampsCustom End converting to PDF", document, version);
      
      return result;
    }
    #endregion

  }
}