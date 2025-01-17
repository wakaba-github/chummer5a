/*  This file is part of Chummer5a.
 *
 *  Chummer5a is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  Chummer5a is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with Chummer5a.  If not, see <http://www.gnu.org/licenses/>.
 *
 *  You can obtain the full source code for Chummer5a at
 *  https://github.com/chummer5a/chummer5a
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Printing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Xsl;
using Codaxy.WkHtmlToPdf;
using Microsoft.Win32;
using NLog;

namespace Chummer
{
    public partial class frmViewer : Form
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private List<Character> _lstCharacters = new List<Character>(1);
        private XmlDocument _objCharacterXml = new XmlDocument { XmlResolver = null };
        private string _strSelectedSheet = GlobalOptions.DefaultCharacterSheet;
        private bool _blnLoading;
        private CultureInfo _objPrintCulture = GlobalOptions.CultureInfo;
        private string _strPrintLanguage = GlobalOptions.Language;
        private readonly BackgroundWorker _workerRefresher = new BackgroundWorker();
        private bool _blnQueueRefresherRun;
        private readonly BackgroundWorker _workerOutputGenerator = new BackgroundWorker();
        private bool _blnQueueOutputGeneratorRun;
        private readonly string _strFilePathName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Guid.NewGuid().ToString("D", GlobalOptions.InvariantCultureInfo) + ".htm");
        #region Control Events
        public frmViewer()
        {
            _workerRefresher.WorkerSupportsCancellation = true;
            _workerRefresher.WorkerReportsProgress = false;
            _workerRefresher.DoWork += AsyncRefresh;
            _workerRefresher.RunWorkerCompleted += FinishRefresh;
            _workerOutputGenerator.WorkerSupportsCancellation = true;
            _workerOutputGenerator.WorkerReportsProgress = false;
            _workerOutputGenerator.DoWork += AsyncGenerateOutput;
            _workerOutputGenerator.RunWorkerCompleted += FinishGenerateOutput;
            if (_strSelectedSheet.StartsWith("Shadowrun 4", StringComparison.Ordinal))
            {
                _strSelectedSheet = GlobalOptions.DefaultCharacterSheetDefaultValue;
            }
            if (GlobalOptions.Language != GlobalOptions.DefaultLanguage)
            {
                if (!_strSelectedSheet.Contains(Path.DirectorySeparatorChar))
                    _strSelectedSheet = Path.Combine(GlobalOptions.Language, _strSelectedSheet);
                else if (!_strSelectedSheet.Contains(GlobalOptions.Language) && _strSelectedSheet.Contains(GlobalOptions.Language.Substring(0, 2)))
                {
                    _strSelectedSheet = _strSelectedSheet.Replace(GlobalOptions.Language.Substring(0, 2), GlobalOptions.Language);
                }
            }
            else
            {
                int intLastIndexDirectorySeparator = _strSelectedSheet.LastIndexOf(Path.DirectorySeparatorChar);
                if (intLastIndexDirectorySeparator != -1 && _strSelectedSheet.Contains(GlobalOptions.Language.Substring(0, 2)))
                    _strSelectedSheet = _strSelectedSheet.Substring(intLastIndexDirectorySeparator + 1);
            }

            using (RegistryKey objRegistry = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Internet Explorer\\Main\\FeatureControl\\FEATURE_BROWSER_EMULATION"))
                objRegistry?.SetValue(AppDomain.CurrentDomain.FriendlyName, GlobalOptions.EmulatedBrowserVersion * 1000, RegistryValueKind.DWord);

            using (RegistryKey objRegistry = Registry.CurrentUser.CreateSubKey("Software\\WOW6432Node\\Microsoft\\Internet Explorer\\Main\\FeatureControl\\FEATURE_BROWSER_EMULATION"))
                objRegistry?.SetValue(AppDomain.CurrentDomain.FriendlyName, GlobalOptions.EmulatedBrowserVersion * 1000, RegistryValueKind.DWord);

            // These two needed to have WebBrowser control obey DPI settings for Chummer
            using (RegistryKey objRegistry = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Internet Explorer\\Main\\FeatureControl\\FEATURE_96DPI_PIXEL"))
                objRegistry?.SetValue(AppDomain.CurrentDomain.FriendlyName, 1, RegistryValueKind.DWord);

            using (RegistryKey objRegistry = Registry.CurrentUser.CreateSubKey("Software\\WOW6432Node\\Microsoft\\Internet Explorer\\Main\\FeatureControl\\FEATURE_96DPI_PIXEL"))
                objRegistry?.SetValue(AppDomain.CurrentDomain.FriendlyName, 1, RegistryValueKind.DWord);

            InitializeComponent();
            this.UpdateLightDarkMode();
            this.TranslateWinForm();
            ContextMenuStrip[] lstCMSToTranslate = {
                cmsPrintButton,
                cmsSaveButton
            };
            foreach (ContextMenuStrip objCMS in lstCMSToTranslate)
            {
                if (objCMS != null)
                {
                    foreach (ToolStripMenuItem tssItem in objCMS.Items.OfType<ToolStripMenuItem>())
                    {
                        tssItem.UpdateLightDarkMode();
                        tssItem.TranslateToolStripItemsRecursively();
                    }
                }
            }
        }

        private void frmViewer_Load(object sender, EventArgs e)
        {
            _blnLoading = true;
            // Populate the XSLT list with all of the XSL files found in the sheets directory.
            LanguageManager.PopulateSheetLanguageList(cboLanguage, _strSelectedSheet, _lstCharacters);
            PopulateXsltList();

            cboXSLT.SelectedValue = _strSelectedSheet;
            // If the desired sheet was not found, fall back to the Shadowrun 5 sheet.
            if (cboXSLT.SelectedIndex == -1)
            {
                string strLanguage = cboLanguage.SelectedValue?.ToString();
                int intNameIndex;
                if (string.IsNullOrEmpty(strLanguage) || strLanguage == GlobalOptions.DefaultLanguage)
                    intNameIndex = cboXSLT.FindStringExact(GlobalOptions.DefaultCharacterSheet);
                else
                    intNameIndex = cboXSLT.FindStringExact(GlobalOptions.DefaultCharacterSheet.Substring(GlobalOptions.DefaultLanguage.LastIndexOf(Path.DirectorySeparatorChar) + 1));
                if (intNameIndex != -1)
                    cboXSLT.SelectedIndex = intNameIndex;
                else if (cboXSLT.Items.Count > 0)
                {
                    if (string.IsNullOrEmpty(strLanguage) || strLanguage == GlobalOptions.DefaultLanguage)
                        _strSelectedSheet = GlobalOptions.DefaultCharacterSheetDefaultValue;
                    else
                        _strSelectedSheet = Path.Combine(strLanguage, GlobalOptions.DefaultCharacterSheetDefaultValue);
                    cboXSLT.SelectedValue = _strSelectedSheet;
                    if (cboXSLT.SelectedIndex == -1)
                    {
                        cboXSLT.SelectedIndex = 0;
                        _strSelectedSheet = cboXSLT.SelectedValue?.ToString();
                    }
                }
            }
            _blnLoading = false;
            SetDocumentText(LanguageManager.GetString("String_Loading_Characters"));

            Application.Idle += RunQueuedWorkers;
        }

        private void RunQueuedWorkers(object sender, EventArgs e)
        {
            if (_blnQueueRefresherRun)
            {
                if (!_workerRefresher.IsBusy)
                    _workerRefresher.RunWorkerAsync();
            }
            else if (_blnQueueOutputGeneratorRun && !_workerOutputGenerator.IsBusy)
            {
                _workerOutputGenerator.RunWorkerAsync();
            }
        }

        private void cboXSLT_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Re-generate the output when a new sheet is selected.
            if (!_blnLoading)
            {
                _strSelectedSheet = cboXSLT.SelectedValue?.ToString() ?? string.Empty;
                RefreshSheet();
            }
        }

        private void cmdPrint_Click(object sender, EventArgs e)
        {
            webViewer.ShowPrintDialog();
        }

        private void tsPrintPreview_Click(object sender, EventArgs e)
        {
            webViewer.ShowPrintPreviewDialog();
        }

        private void tsSaveAsHTML_Click(object sender, EventArgs e)
        {
            // Save the generated output as HTML.
            SaveFileDialog1.Filter = LanguageManager.GetString("DialogFilter_Html") + '|' + LanguageManager.GetString("DialogFilter_All");
            SaveFileDialog1.Title = LanguageManager.GetString("Button_Viewer_SaveAsHtml");
            SaveFileDialog1.ShowDialog();
            string strSaveFile = SaveFileDialog1.FileName;

            if (string.IsNullOrEmpty(strSaveFile))
                return;

            if (!strSaveFile.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                && !strSaveFile.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                strSaveFile += ".htm";

            using (TextWriter objWriter = new StreamWriter(strSaveFile, false, Encoding.UTF8))
                objWriter.Write(webViewer.DocumentText);
        }

        private void tsSaveAsXml_Click(object sender, EventArgs e)
        {
            // Save the printout XML generated by the character.
            SaveFileDialog1.Filter = LanguageManager.GetString("DialogFilter_Xml") + '|' + LanguageManager.GetString("DialogFilter_All");
            SaveFileDialog1.Title = LanguageManager.GetString("Button_Viewer_SaveAsXml");
            SaveFileDialog1.ShowDialog();
            string strSaveFile = SaveFileDialog1.FileName;

            if (string.IsNullOrEmpty(strSaveFile))
                return;

            if (!strSaveFile.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                strSaveFile += ".xml";

            try
            {
                _objCharacterXml.Save(strSaveFile);
            }
            catch (XmlException)
            {
                Program.MainForm.ShowMessageBox(this, LanguageManager.GetString("Message_Save_Error_Warning"));
            }
            catch (UnauthorizedAccessException)
            {
                Program.MainForm.ShowMessageBox(this, LanguageManager.GetString("Message_Save_Error_Warning"));
            }
        }

        private void frmViewer_FormClosing(object sender, FormClosingEventArgs e)
        {
            Application.Idle -= RunQueuedWorkers;

            if (_workerRefresher.IsBusy)
                _workerRefresher.CancelAsync();
            if (_workerOutputGenerator.IsBusy)
                _workerOutputGenerator.CancelAsync();

            // Remove the mugshots directory when the form closes.
            string mugshotsDirectoryPath = Path.Combine(Utils.GetStartupPath, "mugshots");
            if (Directory.Exists(mugshotsDirectoryPath))
            {
                try
                {
                    Directory.Delete(mugshotsDirectoryPath, true);
                }
                catch (IOException)
                {
                }
            }

            // Clear the reference to the character's Print window.
            foreach (CharacterShared objCharacterShared in Program.MainForm.OpenCharacterForms)
                if (objCharacterShared.PrintWindow == this)
                    objCharacterShared.PrintWindow = null;
        }
        #endregion

        #region Methods
        /// <summary>
        /// Set the text of the viewer to something descriptive. Also disables the Print, Print Preview, Save as HTML, and Save as PDF buttons.
        /// </summary>
        private void SetDocumentText(string strText)
        {
            cmdPrint.Enabled = false;
            tsPrintPreview.Enabled = false;
            tsSaveAsHtml.Enabled = false;
            cmdSaveAsPdf.Enabled = false;
            webViewer.DocumentText =
                new StringBuilder("<html xmlns=\"http://www.w3.org/1999/xhtml\" xml:lang=\"en\" lang=\"en\"><head><meta http-equiv=\"x - ua - compatible\" content=\"IE = Edge\"/><meta charset = \"UTF-8\" /></head><body style=\"width:100%;height:")
                    .Append(webViewer.Height.ToString(GlobalOptions.InvariantCultureInfo))
                    .Append(";text-align:center;vertical-align:middle;font-family:segoe, tahoma,'trebuchet ms',arial;font-size:9pt;\">")
                    .Append(strText.CleanForHTML())
                    .Append("</body></html>").ToString();
        }

        /// <summary>
        /// Asynchronously update the characters (and therefore content) of the Viewer window.
        /// </summary>
        public void RefreshCharacters()
        {
            Cursor = Cursors.AppStarting;
            if (_workerOutputGenerator.IsBusy)
                _workerOutputGenerator.CancelAsync();
            if (_workerRefresher.IsBusy)
                _workerRefresher.CancelAsync();
            _blnQueueRefresherRun = true;
        }

        /// <summary>
        /// Asynchronously update the sheet of the Viewer window.
        /// </summary>
        public void RefreshSheet()
        {
            Cursor = Cursors.AppStarting;
            SetDocumentText(LanguageManager.GetString("String_Generating_Sheet"));
            if (_workerOutputGenerator.IsBusy)
                _workerOutputGenerator.CancelAsync();
            _blnQueueOutputGeneratorRun = true;
        }

        /// <summary>
        /// Update the internal XML of the Viewer window.
        /// </summary>
        private void AsyncRefresh(object sender, DoWorkEventArgs e)
        {
            _blnQueueRefresherRun = false;
            if (_lstCharacters.Count <= 0)
            {
                _objCharacterXml = null;
                return;
            }
            // Write the Character information to a MemoryStream so we don't need to create any files.
            using (MemoryStream objStream = new MemoryStream())
            {
                using (XmlTextWriter objWriter = new XmlTextWriter(objStream, Encoding.UTF8))
                {
                    // Begin the document.
                    objWriter.WriteStartDocument();

                    // </characters>
                    objWriter.WriteStartElement("characters");

                    foreach (Character objCharacter in _lstCharacters)
                    {
                        if (_workerRefresher.CancellationPending)
                        {
                            e.Cancel = true;
                            return;
                        }
#if DEBUG
                        objCharacter.PrintToStream(objStream, objWriter, _objPrintCulture, _strPrintLanguage);
#else
                        objCharacter.PrintToStream(objWriter, _objPrintCulture, _strPrintLanguage);
#endif
                    }

                    // </characters>
                    objWriter.WriteEndElement();
                    if (_workerRefresher.CancellationPending)
                    {
                        e.Cancel = true;
                        return;
                    }

                    // Finish the document and flush the Writer and Stream.
                    objWriter.WriteEndDocument();
                    objWriter.Flush();

                    objStream.Position = 0;

                    // Read the stream.
                    XmlDocument objCharacterXml = new XmlDocument { XmlResolver = null };
                    // Read it back in as an XmlDocument.
                    using (StreamReader objReader = new StreamReader(objStream, Encoding.UTF8, true))
                    {
                        using (XmlReader objXmlReader = XmlReader.Create(objReader, GlobalOptions.SafeXmlReaderSettings))
                        {
                            if (_workerRefresher.CancellationPending)
                            {
                                e.Cancel = true;
                                return;
                            }

                            // Put the stream into an XmlDocument and send it off to the Viewer.
                            objCharacterXml.Load(objXmlReader);
                        }
                    }

                    if (_workerRefresher.CancellationPending)
                        e.Cancel = true;
                    else
                        _objCharacterXml = objCharacterXml;
                }
            }
        }

        private void FinishRefresh(object sender, RunWorkerCompletedEventArgs e)
        {
            if (!e.Cancelled)
            {
                tsSaveAsXml.Enabled = _objCharacterXml != null;
                RefreshSheet();
            }
        }

        /// <summary>
        /// Run the generated XML file through the XSL transformation engine to create the file output.
        /// </summary>
        private void AsyncGenerateOutput(object sender, DoWorkEventArgs e)
        {
            _blnQueueOutputGeneratorRun = false;
            string strXslPath = Path.Combine(Utils.GetStartupPath, "sheets", _strSelectedSheet + ".xsl");
            if (!File.Exists(strXslPath))
            {
                string strReturn = "File not found when attempting to load " + _strSelectedSheet + Environment.NewLine;
                Log.Debug(strReturn);
                Program.MainForm.ShowMessageBox(this, strReturn);
                return;
            }
#if DEBUG
            XslCompiledTransform objXslTransform = new XslCompiledTransform(true);
#else
            XslCompiledTransform objXslTransform = new XslCompiledTransform();
#endif
            try
            {
                objXslTransform.Load(strXslPath);
            }
            catch (Exception ex)
            {
                string strReturn = "Error attempting to load " + _strSelectedSheet + Environment.NewLine;
                Log.Debug(strReturn);
                Log.Error("ERROR Message = " + ex.Message);
                strReturn += ex.Message;
                Program.MainForm.ShowMessageBox(this, strReturn);
                return;
            }

            if (_workerOutputGenerator.CancellationPending)
            {
                e.Cancel = true;
                return;
            }

            using (MemoryStream objStream = new MemoryStream())
            {
                using (XmlTextWriter objWriter = new XmlTextWriter(objStream, Encoding.UTF8))
                {
                    objXslTransform.Transform(_objCharacterXml, objWriter);
                    if (_workerOutputGenerator.CancellationPending)
                    {
                        e.Cancel = true;
                        return;
                    }

                    objStream.Position = 0;

                    // This reads from a static file, outputs to an HTML file, then has the browser read from that file. For debugging purposes.
                    //objXSLTransform.Transform("D:\\temp\\print.xml", "D:\\temp\\output.htm");
                    //webBrowser1.Navigate("D:\\temp\\output.htm");

                    if (!GlobalOptions.PrintToFileFirst)
                    {
                        // Populate the browser using DocumentText (DocumentStream would cause issues due to stream disposal).
                        using (StreamReader objReader = new StreamReader(objStream, Encoding.UTF8, true))
                        {
                            webViewer.DocumentText = objReader.ReadToEnd();
                        }
                    }
                    else
                    {
                        // The DocumentStream method fails when using Wine, so we'll instead dump everything out a temporary HTML file, have the WebBrowser load that, then delete the temporary file.
                        // Read in the resulting code and pass it to the browser.

                        using (StreamReader objReader = new StreamReader(objStream, Encoding.UTF8, true))
                        {
                            string strOutput = objReader.ReadToEnd();
                            File.WriteAllText(_strFilePathName, strOutput);
                        }

                        webViewer.Url = new Uri("file:///" + _strFilePathName);
                    }
                }
            }
        }

        private void FinishGenerateOutput(object sender, RunWorkerCompletedEventArgs e)
        {
            if (!e.Cancelled)
            {
                cmdPrint.Enabled = true;
                tsPrintPreview.Enabled = true;
                tsSaveAsHtml.Enabled = true;
                cmdSaveAsPdf.Enabled = true;
            }

            if (GlobalOptions.PrintToFileFirst)
            {
                try
                {
                    File.Delete(_strFilePathName);
                }
                catch (IOException)
                {
                    Utils.BreakIfDebug();
                }
            }

            Cursor = Cursors.Default;
        }

        private void cmdSaveAsPdf_Click(object sender, EventArgs e)
        {
            // Check to see if we have any "Print to PDF" printers, as they will be a lot more reliable than wkhtmltopdf
            string strPdfPrinter = string.Empty;
            foreach (string strPrinter in PrinterSettings.InstalledPrinters)
            {
                if (strPrinter == "Microsoft Print to PDF" || strPrinter == "Foxit Reader PDF Printer" || strPrinter == "Adobe PDF")
                {
                    strPdfPrinter = strPrinter;
                    break;
                }
            }

            if (!string.IsNullOrEmpty(strPdfPrinter))
            {
                DialogResult ePdfPrinterDialogResult = Program.MainForm.ShowMessageBox(this,
                    string.Format(GlobalOptions.CultureInfo, LanguageManager.GetString("Message_Viewer_FoundPDFPrinter"), strPdfPrinter),
                    LanguageManager.GetString("MessageTitle_Viewer_FoundPDFPrinter"),
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
                if (ePdfPrinterDialogResult == DialogResult.Cancel)
                    return;
                if (ePdfPrinterDialogResult == DialogResult.Yes)
                {
                    if (DoPdfPrinterShortcut(strPdfPrinter))
                        return;
                    Program.MainForm.ShowMessageBox(this, LanguageManager.GetString("Message_Viewer_PDFPrinterError"));
                }
            }

            // Save the generated output as PDF.
            SaveFileDialog1.Filter = LanguageManager.GetString("DialogFilter_Pdf") + '|' + LanguageManager.GetString("DialogFilter_All");
            SaveFileDialog1.Title = LanguageManager.GetString("Button_Viewer_SaveAsPdf");
            SaveFileDialog1.ShowDialog();
            string strSaveFile = SaveFileDialog1.FileName;

            if (string.IsNullOrEmpty(strSaveFile))
                return;

            if (!strSaveFile.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                strSaveFile += ".pdf";

            if (!Directory.Exists(Path.GetDirectoryName(strSaveFile)) || !Utils.CanWriteToPath(strSaveFile))
            {
                Program.MainForm.ShowMessageBox(this, LanguageManager.GetString("Message_File_Cannot_Be_Accessed"));
                return;
            }
            if (File.Exists(strSaveFile))
            {
                try
                {
                    File.Delete(strSaveFile);
                }
                catch (IOException)
                {
                    Program.MainForm.ShowMessageBox(this, LanguageManager.GetString("Message_File_Cannot_Be_Accessed"));
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    Program.MainForm.ShowMessageBox(this, LanguageManager.GetString("Message_File_Cannot_Be_Accessed"));
                    return;
                }
            }

            // No PDF printer found, let's use wkhtmltopdf

            PdfDocument objPdfDocument = new PdfDocument
            {
                Html = webViewer.DocumentText
            };
            objPdfDocument.ExtraParams.Add("encoding", "UTF-8");
            objPdfDocument.ExtraParams.Add("dpi", "300");
            objPdfDocument.ExtraParams.Add("margin-top", "13");
            objPdfDocument.ExtraParams.Add("margin-bottom", "19");
            objPdfDocument.ExtraParams.Add("margin-left", "13");
            objPdfDocument.ExtraParams.Add("margin-right", "13");
            objPdfDocument.ExtraParams.Add("image-quality", "100");
            objPdfDocument.ExtraParams.Add("print-media-type", string.Empty);

            try
            {
                PdfConvert.ConvertHtmlToPdf(objPdfDocument, new PdfConvertEnvironment
                {
                    WkHtmlToPdfPath = Path.Combine(Utils.GetStartupPath, "wkhtmltopdf.exe"),
                    Timeout = 60000,
                    TempFolderPath = Path.GetTempPath()
                }, new PdfOutput
                {
                    OutputFilePath = strSaveFile
                });

                if (!string.IsNullOrWhiteSpace(GlobalOptions.PDFAppPath))
                {
                    Uri uriPath = new Uri(strSaveFile);
                    string strParams = GlobalOptions.PDFParameters
                        .Replace("{page}", "1")
                        .Replace("{localpath}", uriPath.LocalPath)
                        .Replace("{absolutepath}", uriPath.AbsolutePath);
                    ProcessStartInfo objPdfProgramProcess = new ProcessStartInfo
                    {
                        FileName = GlobalOptions.PDFAppPath,
                        Arguments = strParams,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    Process.Start(objPdfProgramProcess);
                }
            }
            catch (Exception ex)
            {
                Program.MainForm.ShowMessageBox(this, ex.ToString());
            }
        }



        private bool DoPdfPrinterShortcut(string strPdfPrinterName)
        {
            // We've got a proper, built-in PDF printer, so let's use that instead of wkhtmltopdf
            string strOldHeader = null;
            string strOldFooter = null;
            string strOldPrintBackground = null;
            string strOldShrinkToFit = null;
            string strOldDefaultPrinter = null;
            try
            {
                strOldDefaultPrinter = SystemPrinters.GetDefaultPrinter();
                // Try to remove headers and footers from the printer and set default printer settings to be conducive to sheet printing
                try
                {
                    using (RegistryKey objKey =
                        Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Internet Explorer\\PageSetup", true))
                    {
                        if (objKey != null)
                        {
                            strOldHeader = objKey.GetValue("header")?.ToString();
                            objKey.SetValue("header", string.Empty);
                            strOldFooter = objKey.GetValue("footer")?.ToString();
                            objKey.SetValue("footer", string.Empty);
                            strOldPrintBackground = objKey.GetValue("Print_Background")?.ToString();
                            objKey.SetValue("Print_Background", "yes");
                            strOldShrinkToFit = objKey.GetValue("Shrink_To_Fit")?.ToString();
                            objKey.SetValue("Shrink_To_Fit", "yes");
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }
                catch (SecurityException)
                {
                }

                // webBrowser can only print to the default printer, so we (temporarily) change it to the PDF printer
                if (SystemPrinters.SetDefaultPrinter(strPdfPrinterName))
                {
                    // There is also no way to silently have it print to a PDF, so we have to show the print dialog
                    // and have the user click through, though the PDF printer will be temporarily set as their default
                    webViewer.ShowPrintDialog();
                }
            }
            catch (Exception)
            {
                // Error of some kind occured, proceed to use wkhtmltopdf instead
                return false;
            }
            finally
            {
                if (!string.IsNullOrEmpty(strOldDefaultPrinter))
                    SystemPrinters.SetDefaultPrinter(strOldDefaultPrinter);
                // Try to remove headers and footers from the printer and
                try
                {
                    using (RegistryKey objKey =
                        Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Internet Explorer\\PageSetup", true))
                    {
                        if (objKey != null)
                        {
                            if (strOldHeader != null)
                                objKey.SetValue("header", strOldHeader);
                            if (strOldFooter != null)
                                objKey.SetValue("footer", strOldFooter);
                            if (strOldPrintBackground != null)
                                objKey.SetValue("Print_Background", strOldPrintBackground);
                            if (strOldShrinkToFit != null)
                                objKey.SetValue("Shrink_To_Fit", strOldShrinkToFit);
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }
                catch (SecurityException)
                {
                }
            }

            return true;
        }

        private void PopulateXsltList()
        {
            List<ListItem> lstFiles = XmlManager.GetXslFilesFromLocalDirectory(cboLanguage.SelectedValue?.ToString() ?? GlobalOptions.DefaultLanguage, _lstCharacters);

            cboXSLT.BeginUpdate();
            cboXSLT.DataSource = null;
            cboXSLT.DataSource = lstFiles;
            cboXSLT.ValueMember = nameof(ListItem.Value);
            cboXSLT.DisplayMember = nameof(ListItem.Name);
            cboXSLT.EndUpdate();
        }

        /// <summary>
        /// Set the XSL sheet that will be selected by default.
        /// </summary>
        public void SetSelectedSheet(string strSheet)
        {
            _strSelectedSheet = strSheet;
        }

        /// <summary>
        /// Set List of Characters to print.
        /// </summary>
        public void SetCharacters(params Character[] lstCharacters)
        {
            _lstCharacters = lstCharacters != null ? new List<Character>(lstCharacters) : new List<Character>(1);
        }
        #endregion

        private void cboLanguage_SelectedIndexChanged(object sender, EventArgs e)
        {
            _strPrintLanguage = cboLanguage.SelectedValue?.ToString() ?? GlobalOptions.Language;
            imgSheetLanguageFlag.Image = FlagImageGetter.GetFlagFromCountryCode(_strPrintLanguage.Substring(3, 2));
            try
            {
                _objPrintCulture = CultureInfo.GetCultureInfo(_strPrintLanguage);
            }
            catch (CultureNotFoundException)
            {
            }
            if (_blnLoading)
                return;

            string strOldSelected = _strSelectedSheet;
            // Strip away the language prefix
            if (strOldSelected.Contains(Path.DirectorySeparatorChar))
                strOldSelected = strOldSelected.Substring(strOldSelected.LastIndexOf(Path.DirectorySeparatorChar) + 1);
            _blnLoading = true;
            PopulateXsltList();
            string strNewLanguage = cboLanguage.SelectedValue?.ToString() ?? strOldSelected;
            if (strNewLanguage == strOldSelected)
            {
                _strSelectedSheet = strNewLanguage == GlobalOptions.DefaultLanguage ? strOldSelected : Path.Combine(strNewLanguage, strOldSelected);
            }
            cboXSLT.SelectedValue = _strSelectedSheet;
            // If the desired sheet was not found, fall back to the Shadowrun 5 sheet.
            if (cboXSLT.SelectedIndex == -1)
            {
                var intNameIndex = cboXSLT.FindStringExact(strNewLanguage == GlobalOptions.DefaultLanguage ? GlobalOptions.DefaultCharacterSheet : GlobalOptions.DefaultCharacterSheet.Substring(strNewLanguage.LastIndexOf(Path.DirectorySeparatorChar) + 1));
                if (intNameIndex != -1)
                    cboXSLT.SelectedIndex = intNameIndex;
                else if (cboXSLT.Items.Count > 0)
                {
                    _strSelectedSheet = strNewLanguage == GlobalOptions.DefaultLanguage ? GlobalOptions.DefaultCharacterSheetDefaultValue : Path.Combine(strNewLanguage, GlobalOptions.DefaultCharacterSheetDefaultValue);
                    cboXSLT.SelectedValue = _strSelectedSheet;
                    if (cboXSLT.SelectedIndex == -1)
                    {
                        cboXSLT.SelectedIndex = 0;
                        _strSelectedSheet = cboXSLT.SelectedValue?.ToString();
                    }
                }
            }
            _blnLoading = false;
            RefreshCharacters();
        }

        public static class SystemPrinters
        {
            [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
            public static extern bool SetDefaultPrinter(string strName);

            [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
            public static extern bool GetDefaultPrinter(StringBuilder sbdBuffer, ref int ptrBuffer);

            public static string GetDefaultPrinter()
            {

                int ptrBuffer = 0;
                if (GetDefaultPrinter(null, ref ptrBuffer))
                {
                    return null;
                }
                int intLastWin32Error = Marshal.GetLastWin32Error();
                if (intLastWin32Error == 122) // ERROR_INSUFFICIENT_BUFFER
                {
                    StringBuilder sbdBuffer = new StringBuilder(ptrBuffer);
                    if (GetDefaultPrinter(sbdBuffer, ref ptrBuffer))
                    {
                        return sbdBuffer.ToString();
                    }
                    intLastWin32Error = Marshal.GetLastWin32Error();
                }
                if (intLastWin32Error == 2) // ERROR_FILE_NOT_FOUND
                {
                    return null;
                }
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }
}
