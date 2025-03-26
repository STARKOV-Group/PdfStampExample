using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;
using Starkov.PdfStamp.OfficialDocument;

namespace Starkov.PdfStamp.Server
{
  partial class OfficialDocumentFunctions
  {
    
    #region Расширенная штамповка.
    /// <summary>
    /// Возвращает переменные для использования в PDF штампах, добавлять переменные в наследниках.
    /// </summary>
    public virtual System.Collections.Generic.Dictionary<string, object> GetStampVariables(IStampSetting stampSettings)
    {
      var vars = new Dictionary<string, object>();

      //Ключевая часть магии - шаблонизатор сам бегает по свойствам документа
      vars["doc"] = _obj;
      
      // Условная проверка - на факт регистрации {{#HasRegistered}} {{RegistrationDate}} {{/HasRegistered}}
      if (_obj.RegistrationDate != null)
      {
        vars["IsRegistered"] = new Dictionary<string, object>()
        {
          {"RegistrationDate", _obj.RegistrationDate.Value.ToShortDateString()},
          {"RegistrationNumber", _obj.RegistrationNumber
          }
        };
      }
      
      // Утверждающая подпись основного подписанта
      var signatures = PdfStamp.Module.Docflow.PublicFunctions.Module.GetSignaturesForMark(_obj, _obj.LastVersion?.Id ?? 0, false);
      if (signatures.Any())
      {
        var signatory = _obj.OurSignatory;
        
        //подпись подписанта, может быть может быть по замещению
        var signature = signatures.Where(x => x.SignatureType == SignatureType.Approval)
          .Where(x => signatory != null && (signatory.Equals(x.SubstitutedUser) || signatory.Equals(x.Signatory)))
          .OrderBy(x => x.SigningDate)
          .FirstOrDefault();
        
        if (signature != null)
        {
          var signatureStampParams = Sungero.Docflow.PublicFunctions.StampSetting.GetSignatureStampParams(stampSettings, signature.SigningDate, true);
          var approveVars = PdfStamp.Module.Docflow.PublicFunctions.Module.GetSignatureMarkForCertificateAsDict(signature, stampSettings);
          foreach(var key in approveVars.Keys)
            vars[key] = approveVars[key];
          
          signatures.Remove(signature);
        }
      }
      
      #region Дополнительные согласующие подписи для множественной штамповки.
      var approveSignatures = new List<Dictionary<string, string>>();
      foreach(var signature in signatures)
      {
        var signatureStampParams = Sungero.Docflow.PublicFunctions.StampSetting.GetSignatureStampParams(stampSettings, signature.SigningDate, true);
        var approveVars = PdfStamp.Module.Docflow.PublicFunctions.Module.GetSignatureMarkForCertificateAsDict(signature, stampSettings);
        approveSignatures.Add(approveVars);
      }
      
      if (approveSignatures.Any())
        vars["ApproveSignatures"] = approveSignatures;
      #endregion
      
      return vars;
    }
    
    
    /// <summary>
    /// Возвращает готовые штампы
    /// </summary>
    /// <param name="variables">Переменные {Ключ} = Значение</param>
    /// <returns></returns>
    public virtual System.Collections.Generic.Dictionary<string, string> GetStamps(System.Collections.Generic.Dictionary<string, object> variables, IStampSetting stampSettings)
    {
      var stamps = new Dictionary<string, string>();
      if (stampSettings == null)
      {
        Logger.Debug("PdfStamp GetStamps stampSettings null");
        return stamps;
      }
      
      foreach(var row in stampSettings.HtmlStampsStarkov)
      {
        var anchor = row.Anchor;
        var template = row.Template;
        stamps[anchor] = PdfStamp.Module.Docflow.PublicFunctions.Module.RenderTemplate(template, variables);
      }
      
      return stamps;
    }
    
    /// <summary>
    /// Запуск расширенной штамповки PDF
    /// </summary>
    [Remote]
    public virtual PdfStamp.Structures.Docflow.OfficialDocument.IConversionToPdfResult StampPdf(PdfStamp.IStampSetting stampSettings, long versionId)
    {
      var variables = this.GetStampVariables(stampSettings);
      var stamps = this.GetStamps(variables, stampSettings);
      
      //DEBUG
      Starkov.PdfStamp.Module.Docflow.PublicFunctions.Module.DumpVariables(variables, "");
      Starkov.PdfStamp.Module.Docflow.PublicFunctions.Module.DumpVariables(stamps, "");
      
      return PdfStamp.Module.Docflow.PublicFunctions.Module.ConvertToPdfWithStampsCustom(_obj, versionId, stamps, true);
    }
    
    /// <summary>
    /// Сгенерировать PublicBody документа с отметкой об ЭП.
    /// </summary>
    /// <param name="versionId">ИД версии для генерации.</param>
    /// <param name="signatureMark">Отметка об ЭП (html).</param>
    /// <returns>Информация о результате генерации PublicBody для версии документа.</returns>
    /// 
    /*
    
    public override Sungero.Docflow.Structures.OfficialDocument.IConversionToPdfResult GeneratePublicBodyWithSignatureMark(long versionId, string signatureMark)
    {
      return base.GeneratePublicBodyWithSignatureMark(versionId, signatureMark);
    }
     */
    public override Sungero.Docflow.Structures.OfficialDocument.IConversionToPdfResult GeneratePublicBodyWithSignatureMark(long versionId, string signatureMark)
    {
      var response = Sungero.Docflow.Structures.OfficialDocument.ConversionToPdfResult.Create();
      
      var stampSettings = PdfStamp.StampSettings.As(Sungero.Docflow.PublicFunctions.StampSetting.GetStampSettings(_obj).FirstOrDefault());
      if (stampSettings?.UseExtendedStampStarkov.GetValueOrDefault() ?? false)
      {
        Logger.Debug("GeneratePublicBodyWithSignatureMark Start ExtendedStampPdf");
        var result = this.StampPdf(stampSettings, versionId);
        
        //FIXME: не получается использовать структуру из Docflow  в кастомной функции
        // Ошибка компилятора - Невозможно использовать тип "Sungero.Docflow.Structures.OfficialDocument.ConversionToPdfResult" в функциях с атрибутом "Remote".
        // приходится обычную структуру возвращать и конвертить, возможно я в чем-то не прав
        response.IsFastConvertion = result.IsFastConvertion;
        response.IsOnConvertion = result.IsOnConvertion;
        response.HasErrors = result.HasErrors;
        response.HasConvertionError = result.HasConvertionError;
        response.HasLockError = result.HasLockError;
        response.ErrorTitle = result.ErrorTitle;
        response.ErrorMessage = result.ErrorMessage;
        
        Logger.DebugFormat("GeneratePublicBodyWithSignatureMark Finish ExtendedStampPdf, HasErrors={0}, ErrorTitle={1}, ErrorMessage={2}", result.HasErrors, result.ErrorTitle, result.ErrorMessage);
      }
      else
      {
        Logger.Debug("GeneratePublicBodyWithSignatureMark Start OriginalStampPdf");
        response = base.GeneratePublicBodyWithSignatureMark(versionId, signatureMark);
        Logger.Debug("GeneratePublicBodyWithSignatureMark Finish OriginalStampPdf");
      }
      
      return response;
    }
    #endregion


  }
}