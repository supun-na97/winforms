﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32.System.Com;
using Windows.Win32.System.Ole;
using static Interop;
using static Interop.Mshtml;

namespace System.Windows.Forms
{
    /// <summary>
    ///  This is a wrapper over the native WebBrowser control implemented in shdocvw.dll.
    /// </summary>
    [DefaultProperty(nameof(Url))]
    [DefaultEvent(nameof(DocumentCompleted))]
    [Docking(DockingBehavior.AutoDock)]
    [SRDescription(nameof(SR.DescriptionWebBrowser))]
    [Designer($"System.Windows.Forms.Design.WebBrowserDesigner, {AssemblyRef.SystemDesign}")]
    public partial class WebBrowser : WebBrowserBase
    {
        // Reference to the native ActiveX control's IWebBrowser2
        // Do not reference this directly. Use the AxIWebBrowser2
        // property instead.
        private Mshtml.IWebBrowser2 axIWebBrowser2;

        private AxHost.ConnectionPointCookie _cookie;   // To hook up events from the native WebBrowser
        private Stream documentStreamToSetOnLoad;
        private WebBrowserEncryptionLevel encryptionLevel = WebBrowserEncryptionLevel.Insecure;
        private object objectForScripting;
        private WebBrowserEvent webBrowserEvent;
        internal string statusText = string.Empty;

        private const int WEBBROWSERSTATE_webBrowserShortcutsEnabled = 0x00000001;
        private const int WEBBROWSERSTATE_documentStreamJustSet = 0x00000002;
        private const int WEBBROWSERSTATE_isWebBrowserContextMenuEnabled = 0x00000004;
        private const int WEBBROWSERSTATE_canGoBack = 0x00000008;
        private const int WEBBROWSERSTATE_canGoForward = 0x00000010;
        private const int WEBBROWSERSTATE_scrollbarsEnabled = 0x00000020;
        private const int WEBBROWSERSTATE_allowNavigation = 0x00000040;

        // PERF: take all the bools and put them into a state variable
        private Collections.Specialized.BitVector32 webBrowserState;          // see TREEVIEWSTATE_ consts above

        //
        // 8856f961-340a-11d0-a96b-00c04fd705a2 is the clsid for the native webbrowser control
        //
        /// <summary>
        ///  Creates an instance of the <see cref="WebBrowser"/> control.
        /// </summary>
        public WebBrowser() : base("8856f961-340a-11d0-a96b-00c04fd705a2")
        {
            webBrowserState = new Collections.Specialized.BitVector32(WEBBROWSERSTATE_isWebBrowserContextMenuEnabled |
                    WEBBROWSERSTATE_webBrowserShortcutsEnabled | WEBBROWSERSTATE_scrollbarsEnabled);
            AllowNavigation = true;
        }

        //
        // Public properties:
        //

        /// <summary>
        ///  Specifies whether the WebBrowser control may navigate to another page once
        ///  it has been loaded.  NOTE: it will always be able to navigate before being loaded.
        ///  "Loaded" here means setting Url, DocumentText, or DocumentStream.
        /// </summary>
        [SRDescription(nameof(SR.WebBrowserAllowNavigationDescr))]
        [SRCategory(nameof(SR.CatBehavior))]
        [DefaultValue(true)]
        public bool AllowNavigation
        {
            get
            {
                return webBrowserState[WEBBROWSERSTATE_allowNavigation];
            }
            set
            {
                webBrowserState[WEBBROWSERSTATE_allowNavigation] = value;
                if (webBrowserEvent is not null)
                {
                    webBrowserEvent.AllowNavigation = value;
                }
            }
        }

        /// <summary>
        ///  Specifies whether the WebBrowser control will receive drop notifications.
        ///  Maps to IWebBrowser2:RegisterAsDropTarget.
        ///  Note that this does not mean that the WebBrowser control integrates with
        ///  Windows Forms drag/drop i.e. the DragDrop event does not fire.  It does
        ///  control whether you can drag new documents into the browser control.
        /// </summary>
        [SRDescription(nameof(SR.WebBrowserAllowWebBrowserDropDescr))]
        [SRCategory(nameof(SR.CatBehavior))]
        [DefaultValue(true)]
        public bool AllowWebBrowserDrop
        {
            get
            {
                return AxIWebBrowser2.RegisterAsDropTarget;
            }
            set
            {
                //Note: you lose this value when you load a new document: the value needs to be refreshed in
                //OnDocumentCompleted.
                if (value != AllowWebBrowserDrop)
                {
                    AxIWebBrowser2.RegisterAsDropTarget = value;
                    Refresh();
                }
            }
        }

        /// <summary>
        ///  Specifies whether the browser control shows script errors in dialogs or not.
        ///  Maps to IWebBrowser2:Silent.
        /// </summary>
        [SRDescription(nameof(SR.WebBrowserScriptErrorsSuppressedDescr))]
        [SRCategory(nameof(SR.CatBehavior))]
        [DefaultValue(false)]
        public bool ScriptErrorsSuppressed
        {
            get
            {
                return AxIWebBrowser2.Silent;
            }
            set
            {
                if (value != ScriptErrorsSuppressed)
                {
                    AxIWebBrowser2.Silent = value;
                }
            }
        }

        internal override bool SupportsUiaProviders => true;

        /// <summary>
        ///  Specifies whether the browser control Shortcuts are enabled.
        ///  Maps to IDocHostUIHandler:TranslateAccelerator event.
        /// </summary>
        [SRDescription(nameof(SR.WebBrowserWebBrowserShortcutsEnabledDescr))]
        [SRCategory(nameof(SR.CatBehavior))]
        [DefaultValue(true)]
        public bool WebBrowserShortcutsEnabled
        {
            get
            {
                return webBrowserState[WEBBROWSERSTATE_webBrowserShortcutsEnabled];
            }
            set
            {
                webBrowserState[WEBBROWSERSTATE_webBrowserShortcutsEnabled] = value;
            }
        }

        /// <summary>
        ///  If true, there is navigation history such that calling GoBack() will succeed.
        ///  Defaults to false.  After that it's value is kept up to date by hooking the
        ///  DWebBrowserEvents2:CommandStateChange.
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool CanGoBack
        {
            get
            {
                return CanGoBackInternal;
            }
        }

        /// <summary>
        ///  Returns the current WEBBROWSERSTATE_canGoBack value so that this value can be accessed
        ///  from child classes.
        /// </summary>
        internal bool CanGoBackInternal
        {
            get
            {
                return webBrowserState[WEBBROWSERSTATE_canGoBack];
            }
            set
            {
                if (value != CanGoBackInternal)
                {
                    webBrowserState[WEBBROWSERSTATE_canGoBack] = value;
                    OnCanGoBackChanged(EventArgs.Empty);
                }
            }
        }

        /// <summary>
        ///  If true, there is navigation history such that calling GoForward() will succeed.
        ///  Defaults to false.  After that it's value is kept up to date by hooking the
        ///  DWebBrowserEvents2:CommandStateChange.
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool CanGoForward
        {
            get
            {
                return CanGoForwardInternal;
            }
        }

        /// <summary>
        ///  Returns the current WEBBROWSERSTATE_canGoForward value so that this value can
        ///  be accessed from child classes.
        /// </summary>
        internal bool CanGoForwardInternal
        {
            get
            {
                return webBrowserState[WEBBROWSERSTATE_canGoForward];
            }
            set
            {
                if (value != CanGoForwardInternal)
                {
                    webBrowserState[WEBBROWSERSTATE_canGoForward] = value;
                    OnCanGoForwardChanged(EventArgs.Empty);
                }
            }
        }

        /// <summary>
        ///  The HtmlDocument for page hosted in the html page.  If no page is loaded, it returns null.
        ///  Maps to IWebBrowser2:Document.
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public HtmlDocument Document
        {
            get
            {
                object objDoc = AxIWebBrowser2.Document;
                if (objDoc is not null)
                {
                    // Document is not necessarily an IHTMLDocument, it might be an office document as well.
                    IHTMLDocument2 iHTMLDocument2 = null;
                    try
                    {
                        iHTMLDocument2 = objDoc as IHTMLDocument2;
                    }
                    catch (InvalidCastException)
                    {
                    }

                    if (iHTMLDocument2 is not null)
                    {
                        IHTMLLocation iHTMLLocation = iHTMLDocument2.GetLocation();
                        if (iHTMLLocation is not null)
                        {
                            string href = iHTMLLocation.GetHref();
                            if (!string.IsNullOrEmpty(href))
                            {
                                Uri url = new Uri(href);
                                return new HtmlDocument(ShimManager, iHTMLDocument2 as IHTMLDocument);
                            }
                        }
                    }
                }

                return null;
            }
        }

        /// <summary>
        ///  Get/sets the stream for the html document.
        ///  Uses the IPersistStreamInit interface on the HtmlDocument to set/retrieve the html stream.
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public unsafe Stream DocumentStream
        {
            get
            {
                HtmlDocument htmlDocument = Document;
                if (htmlDocument is null)
                {
                    return null;
                }
                else
                {
                    IPersistStreamInit.Interface psi = htmlDocument.DomDocument as IPersistStreamInit.Interface;
                    Debug.Assert(psi is not null, "Object isn't an IPersistStreamInit!");
                    if (psi is null)
                    {
                        return null;
                    }
                    else
                    {
                        MemoryStream memoryStream = new MemoryStream();
                        using var pStream = ComHelpers.GetComScope<IStream>(new Ole32.GPStream(memoryStream));
                        psi.Save(pStream, fClearDirty: false);
                        return new MemoryStream(memoryStream.GetBuffer(), 0, (int)memoryStream.Length, false);
                    }
                }
            }
            set
            {
                documentStreamToSetOnLoad = value;
                try
                {
                    webBrowserState[WEBBROWSERSTATE_documentStreamJustSet] = true;
                    // Lets navigate to "about:blank" so that we get a "clean" document
                    Url = new Uri("about:blank");
                }
                finally
                {
                    webBrowserState[WEBBROWSERSTATE_documentStreamJustSet] = false;
                }
            }
        }

        /// <summary>
        ///  Sets/sets the text of the contained html page.
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string DocumentText
        {
            get
            {
                using Stream stream = DocumentStream;
                if (stream is null)
                {
                    return string.Empty;
                }

                using var reader = new StreamReader(stream);
                stream.Position = 0;
                return reader.ReadToEnd().TrimEnd('\0');
            }
            set
            {
                value ??= string.Empty;

                //string length is a good initial guess for capacity --
                //if it needs more room, it'll take it.
                MemoryStream ms = new MemoryStream(value.Length);
                StreamWriter sw = new StreamWriter(ms, Encoding.UTF8);
                sw.Write(value);
                sw.Flush();
                ms.Position = 0;
                DocumentStream = ms;
            }
        }

        /// <summary>
        ///  The title of the html page currently loaded. If none are loaded, returns empty string.
        ///  Maps to IWebBrowser2:LocationName.
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string DocumentTitle
        {
            get
            {
                string documentTitle;

                HtmlDocument htmlDocument = Document;
                if (htmlDocument is null)
                {
                    documentTitle = AxIWebBrowser2.LocationName;
                }
                else
                {
                    IHTMLDocument2 htmlDocument2 = htmlDocument.DomDocument as IHTMLDocument2;
                    Debug.Assert(htmlDocument2 is not null, "The HtmlDocument object must implement IHTMLDocument2.");
                    try
                    {
                        documentTitle = htmlDocument2.GetTitle();
                    }
                    catch (COMException)
                    {
                        documentTitle = string.Empty;
                    }
                }

                return documentTitle;
            }
        }

        /// <summary>
        ///  A string containing the MIME type of the document hosted in the browser control.
        ///  If none are loaded, returns empty string.  Maps to IHTMLDocument2:mimeType.
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string DocumentType
        {
            get
            {
                string docType = string.Empty;
                HtmlDocument htmlDocument = Document;
                if (htmlDocument is not null)
                {
                    IHTMLDocument2 htmlDocument2 = htmlDocument.DomDocument as IHTMLDocument2;
                    Debug.Assert(htmlDocument2 is not null, "The HtmlDocument object must implement IHTMLDocument2.");
                    try
                    {
                        docType = htmlDocument2.GetMimeType();
                    }
                    catch (COMException)
                    {
                        docType = string.Empty;
                    }
                }

                return docType;
            }
        }

        /// <summary>
        ///  Initially set to WebBrowserEncryptionLevel.Insecure.
        ///  After that it's kept up to date by hooking the DWebBrowserEvents2:SetSecureLockIcon.
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public WebBrowserEncryptionLevel EncryptionLevel
        {
            get
            {
                if (Document is null)
                {
                    encryptionLevel = WebBrowserEncryptionLevel.Unknown;
                }

                return encryptionLevel;
            }
        }

        /// <summary>
        ///  True if the browser is engaged in navigation or download.  Maps to IWebBrowser2:Busy.
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool IsBusy
        {
            get
            {
                if (Document is null)
                {
                    return false;
                }
                else
                {
                    return AxIWebBrowser2.Busy;
                }
            }
        }

        /// <summary>
        ///  Gets the offline state of the browser control. Maps to IWebBrowser2:Offline.
        /// </summary>
        [SRDescription(nameof(SR.WebBrowserIsOfflineDescr))]
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool IsOffline
        {
            get
            {
                return AxIWebBrowser2.Offline;
            }
        }

        /// <summary>
        ///  Indicates whether to use the WebBrowser context menu.
        ///  It's technically possible to have both the WebBrowser &amp; Windows Forms context
        ///  menu enabled, but making this property effect the behavior of the Windows Form
        ///  context menu does not lead to a clean OM.  Maps to sinking the
        ///  IDocHostUIHandler:ShowContextMenu
        /// </summary>
        [SRDescription(nameof(SR.WebBrowserIsWebBrowserContextMenuEnabledDescr))]
        [SRCategory(nameof(SR.CatBehavior))]
        [DefaultValue(true)]
        public bool IsWebBrowserContextMenuEnabled
        {
            get
            {
                return webBrowserState[WEBBROWSERSTATE_isWebBrowserContextMenuEnabled];
            }
            set
            {
                webBrowserState[WEBBROWSERSTATE_isWebBrowserContextMenuEnabled] = value;
            }
        }

        /// <summary>
        ///  Allows the host application to provide an object that the contained html
        ///  pages can access programmatically in script.  The object specified here
        ///  will be accessible in script as the "window.external" object via IDispatch
        ///  COM interop. Maps to an implementation of the IDocUIHandler.GetExternal event.
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public object ObjectForScripting
        {
            get
            {
                return objectForScripting;
            }
            set
            {
                if (value is not null)
                {
                    if (!Marshal.IsTypeVisibleFromCom(value.GetType()))
                    {
                        throw new ArgumentException(SR.WebBrowserObjectForScriptingComVisibleOnly, nameof(value));
                    }
                }

                objectForScripting = value;
            }
        }

        [Browsable(false)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public new Padding Padding
        {
            get => base.Padding;
            set => base.Padding = value;
        }

        [Browsable(false)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [SRCategory(nameof(SR.CatLayout))]
        [SRDescription(nameof(SR.ControlOnPaddingChangedDescr))]
        public new event EventHandler PaddingChanged
        {
            add => base.PaddingChanged += value;
            remove => base.PaddingChanged -= value;
        }

        /// <summary>
        ///  Gets the ReadyState of the browser control. (ex.. document loading vs. load complete).
        ///  Maps to IWebBrowser2:ReadyState.
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public WebBrowserReadyState ReadyState
        {
            get
            {
                if (Document is null)
                {
                    return WebBrowserReadyState.Uninitialized;
                }
                else
                {
                    return (WebBrowserReadyState)AxIWebBrowser2.ReadyState;
                }
            }
        }

        /// <summary>
        ///  The text that would be displayed in the IE status bar.
        ///  There is no direct WebBrowser property that maps to this. This property is
        ///  initially an empty string.  After that the value is kept up to date via the
        ///  DWebBrowserEvents2:StatusTextChange event.
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public virtual string StatusText
        {
            get
            {
                if (Document is null)
                {
                    statusText = string.Empty;
                }

                return statusText;
            }
        }

        /// <summary>
        ///  The url of the HtmlDocument for page hosted in the html page.
        ///  Get Maps to IWebBrowser2:LocationUrl.  Set is the equivalent of calling Navigate(Url).
        ///  Note this means that setting the Url property &amp; then reading it immediately may not
        ///  return the result that you just set (since the get always returns the url you are currently at).
        /// </summary>
        [SRDescription(nameof(SR.WebBrowserUrlDescr))]
        [Bindable(true)]
        [SRCategory(nameof(SR.CatBehavior))]
        [TypeConverter(typeof(WebBrowserUriTypeConverter))]
        [DefaultValue(null)]
        public Uri Url
        {
            get
            {
                string urlString = AxIWebBrowser2.LocationURL;

                if (string.IsNullOrEmpty(urlString))
                {
                    return null;
                }

                try
                {
                    return new Uri(urlString);
                }
                catch (UriFormatException)
                {
                    return null;
                }
            }
            set
            {
                if (value is not null && value.ToString().Length == 0)
                {
                    value = null;
                }

                PerformNavigateHelper(ReadyNavigateToUrl(value), false, null, null, null);
            }
        }

        /// <summary>
        ///  Returns the version property of IE.
        ///  Determined by reading the file version of mshtml.dll in the %system% directory.
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Version Version
        {
            get
            {
                string mshtmlPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mshtml.dll");
                FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(mshtmlPath);
                return new Version(fvi.FileMajorPart, fvi.FileMinorPart, fvi.FileBuildPart, fvi.FilePrivatePart);
            }
        }

        //
        // Public methods:
        //

        /// <summary>
        ///  Navigates the browser to the previous page in the navigation history list.
        ///  Maps to IWebBrowser2:GoBack.
        ///  Returns true if the operation succeeds, else returns false.  It will return
        ///  false if there is no page in the navigation history to go back to.
        /// </summary>
        public bool GoBack()
        {
            bool retVal = true;
            try
            {
                AxIWebBrowser2.GoBack();
            }
            catch (Exception ex) when (!ClientUtils.IsCriticalException(ex))
            {
                retVal = false;
            }

            return retVal;
        }

        /// <summary>
        ///  Navigates the browser to the next page in the navigation history list.
        ///  Maps to IWebBrowser2:GoForward.
        ///  Returns true if the operation succeeds, else returns false.  It will return
        ///  false if there is no page in the navigation history to go forward to.
        /// </summary>
        public bool GoForward()
        {
            bool retVal = true;
            try
            {
                AxIWebBrowser2.GoForward();
            }
            catch (Exception ex) when (!ClientUtils.IsCriticalException(ex))
            {
                retVal = false;
            }

            return retVal;
        }

        /// <summary>
        ///  Navigates the browser to user's homepage.  Maps to IWebBrowser2:GoHome.
        /// </summary>
        public void GoHome()
        {
            AxIWebBrowser2.GoHome();
        }

        /// <summary>
        ///  Navigates the browser to user's default search page.  Maps to IWebBrowser2:GoSearch.
        /// </summary>
        public void GoSearch()
        {
            AxIWebBrowser2.GoSearch();
        }

        /// <summary>
        ///  Navigates to the specified Uri's AbsolutePath
        /// </summary>
        public void Navigate(Uri url)
        {
            Url = url; // Does null check in PerformNavigate2
        }

        /// <summary>
        ///  String overload for Navigate(Uri)
        /// </summary>
        ///  Note: We intentionally have a string overload (apparently Mort wants one).  We don't have
        ///  string overloads call Uri overloads because that breaks Uris that aren't fully qualified
        ///  (things like "www.microsoft.com") that the underlying objects support and we don't want to
        ///  break.
        public void Navigate(string urlString)
        {
            PerformNavigateHelper(ReadyNavigateToUrl(urlString), false, null, null, null);
        }

        /// <summary>
        ///  Navigates the specified frame to the specified URL.
        ///  If the frame name is invalid, it opens a new window (not ideal, but it's the current behavior).
        ///  Maps to IWebBrowser2:Navigate.
        /// </summary>
        public void Navigate(Uri url, string targetFrameName)
        {
            PerformNavigateHelper(ReadyNavigateToUrl(url), false, targetFrameName, null, null);
        }

        /// <summary>
        ///  String overload for Navigate(Uri, string)
        /// </summary>
        ///  Note: We intentionally have a string overload (apparently Mort wants one).  We don't have
        ///  string overloads call Uri overloads because that breaks Uris that aren't fully qualified
        ///  (things like "www.microsoft.com") that the underlying objects support and we don't want to
        ///  break.
        public void Navigate(string urlString, string targetFrameName)
        {
            PerformNavigateHelper(ReadyNavigateToUrl(urlString), false, targetFrameName, null, null);
        }

        /// <summary>
        ///  Opens a new window if newWindow is true, navigating it to the specified URL. Maps to IWebBrowser2:Navigate.
        /// </summary>
        public void Navigate(Uri url, bool newWindow)
        {
            PerformNavigateHelper(ReadyNavigateToUrl(url), newWindow, null, null, null);
        }

        /// <summary>
        ///  String overload for Navigate(Uri, bool)
        /// </summary>
        ///  Note: We intentionally have a string overload (apparently Mort wants one).  We don't have
        ///  string overloads call Uri overloads because that breaks Uris that aren't fully qualified
        ///  (things like "www.microsoft.com") that the underlying objects support and we don't want to
        ///  break.
        public void Navigate(string urlString, bool newWindow)
        {
            PerformNavigateHelper(ReadyNavigateToUrl(urlString), newWindow, null, null, null);
        }

        /// <summary>
        ///  Navigates to the specified Uri's AbsolutePath with specified args
        /// </summary>
        public void Navigate(Uri url, string targetFrameName, byte[] postData, string additionalHeaders)
        {
            PerformNavigateHelper(ReadyNavigateToUrl(url), false, targetFrameName, postData, additionalHeaders);
        }

        /// <summary>
        ///  String overload for Navigate(Uri, string, byte[], string)
        /// </summary>
        ///  Note: We intentionally have a string overload (apparently Mort wants one).  We don't have
        ///  string overloads call Uri overloads because that breaks Uris that aren't fully qualified
        ///  (things like "www.microsoft.com") that the underlying objects support and we don't want to
        ///  break.
        public void Navigate(string urlString, string targetFrameName, byte[] postData, string additionalHeaders)
        {
            PerformNavigateHelper(ReadyNavigateToUrl(urlString), false, targetFrameName, postData, additionalHeaders);
        }

        /// <summary>
        ///  Prints the html document to the default printer w/ no print dialog.
        ///  Maps to IWebBrowser2:ExecWB w/ IDM_PRINT flag &amp; LECMDEXECOPT_DONTPROMPTUSER.
        /// </summary>
        public void Print()
        {
            try
            {
                AxIWebBrowser2.ExecWB(OLECMDID.OLECMDID_PRINT, OLECMDEXECOPT.OLECMDEXECOPT_DONTPROMPTUSER, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception ex) when (!ClientUtils.IsCriticalException(ex))
            {
            }
        }

        /// <summary>
        ///  Refreshes the current page.  Maps to IWebBrowser2:Refresh.
        /// </summary>
        public override void Refresh()
        {
            try
            {
                if (ShouldSerializeDocumentText())
                {
                    string text = DocumentText;
                    AxIWebBrowser2.Refresh();
                    DocumentText = text;
                }
                else
                {
                    AxIWebBrowser2.Refresh();
                }
            }
            catch (Exception ex) when (!ClientUtils.IsCriticalException(ex))
            {
            }
        }

        /// <summary>
        ///  Refreshes the current page w/ the specified refresh option. The refresh option
        ///  controls how much is loaded out of the browser cache vs. rechecking the server for.
        ///  Maps to IWebBrowser2:Refresh2
        /// </summary>
        public void Refresh(WebBrowserRefreshOption opt)
        {
            object level = (object)opt;
            try
            {
                if (ShouldSerializeDocumentText())
                {
                    string text = DocumentText;
                    AxIWebBrowser2.Refresh2(ref level);
                    DocumentText = text;
                }
                else
                {
                    AxIWebBrowser2.Refresh2(ref level);
                }
            }
            catch (Exception ex) when (!ClientUtils.IsCriticalException(ex))
            {
            }
        }

        /// <summary>
        ///  Enables/disables the webbrowser's scrollbars.
        /// </summary>
        [SRDescription(nameof(SR.WebBrowserScrollBarsEnabledDescr))]
        [SRCategory(nameof(SR.CatBehavior))]
        [DefaultValue(true)]
        public bool ScrollBarsEnabled
        {
            get
            {
                return webBrowserState[WEBBROWSERSTATE_scrollbarsEnabled];
            }
            set
            {
                if (value != webBrowserState[WEBBROWSERSTATE_scrollbarsEnabled])
                {
                    webBrowserState[WEBBROWSERSTATE_scrollbarsEnabled] = value;
                    Refresh();
                }
            }
        }

        /// <summary>
        ///  Opens the IE page setup dialog for the current page.
        ///  Maps to IWebBrowser2:ExecWebBrowser w/ IDM_PAGESETUP flag &amp; LECMDEXECOPT_PROMPTUSER.
        /// </summary>
        public void ShowPageSetupDialog()
        {
            try
            {
                AxIWebBrowser2.ExecWB(OLECMDID.OLECMDID_PAGESETUP, OLECMDEXECOPT.OLECMDEXECOPT_PROMPTUSER, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception ex) when (!ClientUtils.IsCriticalException(ex))
            {
            }
        }

        /// <summary>
        ///  Opens the IE print dialog.
        ///  Maps to IWebBrowser2:ExecWebBrowser w/ IDM_PRINT flag &amp; OLECMDEXECOPT_PROMPTUSER.
        /// </summary>
        public void ShowPrintDialog()
        {
            try
            {
                AxIWebBrowser2.ExecWB(OLECMDID.OLECMDID_PRINT, OLECMDEXECOPT.OLECMDEXECOPT_PROMPTUSER, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception ex) when (!ClientUtils.IsCriticalException(ex))
            {
            }
        }

        /// <summary>
        ///  Opens the IE print preview dialog.  Maps to IWebBrowser2:ExecWebBrowser w/ IDM_PRINTPREVIEW flag.
        /// </summary>
        public void ShowPrintPreviewDialog()
        {
            try
            {
                AxIWebBrowser2.ExecWB(OLECMDID.OLECMDID_PRINTPREVIEW, OLECMDEXECOPT.OLECMDEXECOPT_PROMPTUSER, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception ex) when (!ClientUtils.IsCriticalException(ex))
            {
            }
        }

        /// <summary>
        ///  Opens the properties dialog for the current html page.
        ///  Maps to IWebBrowser2:ExecWebBrowser w/ IDM_PROPERTIES flag &amp; LECMDEXECOPT_PROMPTUSER.
        /// </summary>
        public void ShowPropertiesDialog()
        {
            try
            {
                AxIWebBrowser2.ExecWB(OLECMDID.OLECMDID_PROPERTIES, OLECMDEXECOPT.OLECMDEXECOPT_PROMPTUSER, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception ex) when (!ClientUtils.IsCriticalException(ex))
            {
            }
        }

        /// <summary>
        ///  Opens the IE File-Save dialog.
        ///  Maps to IWebBrowser2:ExecWebBrowser w/ IDM_SAVEAS flag &amp; LECMDEXECOPT_PROMPTUSER.
        /// </summary>
        public void ShowSaveAsDialog()
        {
            try
            {
                AxIWebBrowser2.ExecWB(OLECMDID.OLECMDID_SAVEAS, OLECMDEXECOPT.OLECMDEXECOPT_DODEFAULT, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception ex) when (!ClientUtils.IsCriticalException(ex))
            {
            }
        }

        /// <summary>
        ///  Stops the current navigation.  Maps to IWebBrowser2:Stop.
        /// </summary>
        public void Stop()
        {
            try
            {
                AxIWebBrowser2.Stop();
            }
            catch (Exception ex) when (!ClientUtils.IsCriticalException(ex))
            {
            }
        }

        /// <summary>
        ///  Occurs when the IE back button would change from enabled to disabled or vice versa.
        ///  Maps to DWebBrowserEvents2:CommandStateChange w/ SHDocVw.CSC.NAVIGATEBACK.
        /// </summary>
        [Browsable(false)]
        [SRCategory(nameof(SR.CatPropertyChanged))]
        [SRDescription(nameof(SR.WebBrowserCanGoBackChangedDescr))]
        public event EventHandler CanGoBackChanged;

        /// <summary>
        ///  Occurs when the IE forward button would change from enabled to disabled or vice versa.
        ///  Maps to DWebBrowserEvents2:CommandStateChange w/ SHDocVw.CSC.NAVIGATEFORWARD.
        /// </summary>
        [Browsable(false)]
        [SRCategory(nameof(SR.CatPropertyChanged))]
        [SRDescription(nameof(SR.WebBrowserCanGoForwardChangedDescr))]
        public event EventHandler CanGoForwardChanged;

        /// <summary>
        ///  Occurs when the document hosted in the web browser control is fully loaded.
        ///  This is conceptually similar to Form.Load().  You need to wait until this event fires
        ///  before doing anything that manipulates the html page, ex. reading the Document
        ///  property of the webbrowser control. Maps to DWebBrowserEvents2:DocumentComplete.
        /// </summary>
        [SRCategory(nameof(SR.CatBehavior))]
        [SRDescription(nameof(SR.WebBrowserDocumentCompletedDescr))]
        public event WebBrowserDocumentCompletedEventHandler DocumentCompleted;

        /// <summary>
        ///  Occurs whenever the title text changes. The Title is the html page title
        ///  or the file path/url if not title is available. This is the text you see as
        ///  the title of the IE window preceeding "Microsoft Internet Explorer".
        ///  Maps to DWebBrowserEvents2:TitleChange.
        /// </summary>
        [Browsable(false)]
        [SRCategory(nameof(SR.CatPropertyChanged))]
        [SRDescription(nameof(SR.WebBrowserDocumentTitleChangedDescr))]
        public event EventHandler DocumentTitleChanged;

        /// <summary>
        ///  Occurs whenever encryption level changes.
        ///  Can be used to set a custom security lock icon similar to what IE shows when
        ///  you go to an https site. Maps to DWebBrowserEvents2:SetSecureLockIcon.
        /// </summary>
        [Browsable(false)]
        [SRCategory(nameof(SR.CatPropertyChanged))]
        [SRDescription(nameof(SR.WebBrowserEncryptionLevelChangedDescr))]
        public event EventHandler EncryptionLevelChanged;

        /// <summary>
        ///  Occurs when a file download occurs.
        ///  Can be used to cancel file downloads. Maps to DWebBrowserEvents2:FileDownload.
        /// </summary>
        [SRCategory(nameof(SR.CatBehavior))]
        [SRDescription(nameof(SR.WebBrowserFileDownloadDescr))]
        public event EventHandler FileDownload;

        /// <summary>
        ///  Occurs after browser control navigation occurs.
        ///  Fires after browser navigation is complete. Maps to DWebBrowserEvents2:NavigateComplete.
        /// </summary>
        [SRCategory(nameof(SR.CatAction))]
        [SRDescription(nameof(SR.WebBrowserNavigatedDescr))]
        public event WebBrowserNavigatedEventHandler Navigated;

        /// <summary>
        ///  Occurs before browser control navigation occurs.
        ///  Fires before browser navigation occurs. Allows navigation to be canceled if
        ///  NavigatingEventArgs.Cancel is set to false. Maps to DWebBrowserEvents2:BeforeNavigate2.
        /// </summary>
        [SRCategory(nameof(SR.CatAction))]
        [SRDescription(nameof(SR.WebBrowserNavigatingDescr))]
        public event WebBrowserNavigatingEventHandler Navigating;

        /// <summary>
        ///  Occurs when a new browser window is created.
        ///  Can be used to cancel the creation of the new browser window. Maps to DWebBrowserEvents2:NewWindow2.
        /// </summary>
        [SRCategory(nameof(SR.CatAction))]
        [SRDescription(nameof(SR.WebBrowserNewWindowDescr))]
        public event CancelEventHandler NewWindow;

        /// <summary>
        ///  Occurs when an update to the progress of a download occurs.
        ///  Fires whenever the browser control has updated info on the download. Can be
        ///  used to provide a download status bar and display the number of bytes downloaded.
        ///  Maps to DWebBrowserEvents2:ProgressChange.
        /// </summary>
        [SRCategory(nameof(SR.CatAction))]
        [SRDescription(nameof(SR.WebBrowserProgressChangedDescr))]
        public event WebBrowserProgressChangedEventHandler ProgressChanged;

        /// <summary>
        ///  Occurs whenever the status text changes.
        ///  Can be used to keep a status bar populated with uptodate text.
        ///  Maps to DWebBrowserEvents2:StatusTextChange.
        /// </summary>
        [Browsable(false)]
        [SRCategory(nameof(SR.CatPropertyChanged))]
        [SRDescription(nameof(SR.WebBrowserStatusTextChangedDescr))]
        public event EventHandler StatusTextChanged;

        /// <summary>
        ///  Returns true if this control (or any of its child windows) has focus.
        /// </summary>
        public override bool Focused
        {
            get
            {
                if (base.Focused)
                {
                    return true;
                }

                HWND hwndFocus = PInvoke.GetFocus();
                return !hwndFocus.IsNull && PInvoke.IsChild(this, hwndFocus);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                htmlShimManager?.Dispose();

                DetachSink();
                ActiveXSite.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <summary>
        ///  Overrides the default size property of Control to specify a bigger default size of 250 x 250.
        /// </summary>
        protected override Size DefaultSize
        {
            get
            {
                return new Size(250, 250);
            }
        }

        /// <summary>
        ///  Retrieves IWebBrowser2 from the native object. Overriding classes should first call base.AttachInterfaces.
        /// </summary>
        protected override void AttachInterfaces(object nativeActiveXObject)
        {
            axIWebBrowser2 = (Mshtml.IWebBrowser2)nativeActiveXObject;
        }

        /// <summary>
        ///  Discards the IWebBrowser2 reference. Overriding classes should call base.DetachInterfaces.
        /// </summary>
        protected override void DetachInterfaces()
        {
            axIWebBrowser2 = null;
        }

        protected override AccessibleObject CreateAccessibilityInstance() => new WebBrowserAccessibleObject(this);

        /// <summary>
        ///  Attaches to the DWebBrowserEvents2 connection point.
        /// </summary>
        protected override void CreateSink()
        {
            if (_activeXInstance is { } ax)
            {
                _cookie?.Disconnect();

                webBrowserEvent = new WebBrowserEvent(this)
                {
                    AllowNavigation = AllowNavigation
                };

                _cookie = new AxHost.ConnectionPointCookie(ax, webBrowserEvent, typeof(SHDocVw.DWebBrowserEvents2));
            }
        }

        /// <summary>
        ///  Releases the DWebBrowserEvents2 connection point.
        /// </summary>
        protected override void DetachSink()
        {
            // If we have a cookie get rid of it
            if (_cookie is not null)
            {
                _cookie.Disconnect();
                _cookie = null;
            }
        }

        /// <summary>
        ///  Returns a WebBrowserSite object.
        /// </summary>
        protected override WebBrowserSiteBase CreateWebBrowserSiteBase()
        {
            return new WebBrowserSite(this);
        }

        /// <summary>
        ///  Raises the <see cref="CanGoBackChanged"/> event.
        /// </summary>
        //
        protected virtual void OnCanGoBackChanged(EventArgs e)
        {
            CanGoBackChanged?.Invoke(this, e);
        }

        /// <summary>
        ///  Raises the <see cref="CanGoForwardChanged"/> event.
        /// </summary>
        //
        protected virtual void OnCanGoForwardChanged(EventArgs e)
        {
            CanGoForwardChanged?.Invoke(this, e);
        }

        /// <summary>
        ///  Raises the <see cref="DocumentCompleted"/> event.
        /// </summary>
        //
        protected virtual void OnDocumentCompleted(WebBrowserDocumentCompletedEventArgs e)
        {
            AxIWebBrowser2.RegisterAsDropTarget = AllowWebBrowserDrop;
            DocumentCompleted?.Invoke(this, e);
        }

        /// <summary>
        ///  Raises the <see cref="DocumentTitleChanged"/> event.
        /// </summary>
        //
        protected virtual void OnDocumentTitleChanged(EventArgs e)
        {
            DocumentTitleChanged?.Invoke(this, e);
        }

        /// <summary>
        ///  Raises the <see cref="EncryptionLevelChanged"/> event.
        /// </summary>
        //
        protected virtual void OnEncryptionLevelChanged(EventArgs e)
        {
            EncryptionLevelChanged?.Invoke(this, e);
        }

        /// <summary>
        ///  Raises the <see cref="FileDownload"/> event.
        /// </summary>
        //
        protected virtual void OnFileDownload(EventArgs e)
        {
            FileDownload?.Invoke(this, e);
        }

        /// <summary>
        ///  Raises the <see cref="Navigated"/> event.
        /// </summary>
        //
        protected virtual void OnNavigated(WebBrowserNavigatedEventArgs e)
        {
            Navigated?.Invoke(this, e);
        }

        /// <summary>
        ///  Raises the <see cref="Navigating"/> event.
        /// </summary>
        //
        protected virtual void OnNavigating(WebBrowserNavigatingEventArgs e)
        {
            Navigating?.Invoke(this, e);
        }

        /// <summary>
        ///  Raises the <see cref="NewWindow"/> event.
        /// </summary>
        //
        protected virtual void OnNewWindow(CancelEventArgs e)
        {
            NewWindow?.Invoke(this, e);
        }

        /// <summary>
        ///  Raises the <see cref="ProgressChanged"/> event.
        /// </summary>
        //
        protected virtual void OnProgressChanged(WebBrowserProgressChangedEventArgs e)
        {
            ProgressChanged?.Invoke(this, e);
        }

        /// <summary>
        ///  Raises the <see cref="StatusTextChanged"/> event.
        /// </summary>
        //
        protected virtual void OnStatusTextChanged(EventArgs e)
        {
            StatusTextChanged?.Invoke(this, e);
        }

        #region ShimSupport
        private HtmlShimManager htmlShimManager;
        internal HtmlShimManager ShimManager
        {
            get
            {
                htmlShimManager ??= new HtmlShimManager();

                return htmlShimManager;
            }
        }
        #endregion

        private string ReadyNavigateToUrl(string urlString)
        {
            if (string.IsNullOrEmpty(urlString))
            {
                urlString = "about:blank";
            }

            //
            // Nullify any calls to set_DocumentStream which may still be pending
            if (!webBrowserState[WEBBROWSERSTATE_documentStreamJustSet])
            {
                documentStreamToSetOnLoad = null;
            }

            return urlString;
        }

        private string ReadyNavigateToUrl(Uri url)
        {
            string urlString;
            if (url is null)
            {
                urlString = "about:blank";
            }
            else
            {
                if (!url.IsAbsoluteUri)
                {
                    throw new ArgumentException(string.Format(SR.WebBrowserNavigateAbsoluteUri, "uri"));
                }

                // Characters outside of US-ASCII may appear in Windows file paths and accordingly they are allowed in file URIs.
                // Therefore, do not use the escaped AbsoluteUri for file schemes. Can't use ToString() either since the correct
                // syntax for file schemas includes percent escaped characters. We are stuck with OriginalString and hope that
                // it is well-formed.
                urlString = url.IsFile ? url.OriginalString : url.AbsoluteUri;
            }

            return ReadyNavigateToUrl(urlString);
        }

        private void PerformNavigateHelper(string urlString, bool newWindow, string targetFrameName, byte[] postData, string headers)
        {
            object objUrlString = (object)urlString;
            object objFlags = (object)(newWindow ? 1 : 0);
            object objTargetFrameName = (object)targetFrameName;
            object objPostData = (object)postData;
            object objHeaders = (object)headers;
            PerformNavigate2(ref objUrlString, ref objFlags, ref objTargetFrameName, ref objPostData, ref objHeaders);
        }

        private void PerformNavigate2(ref object URL, ref object flags, ref object targetFrameName, ref object postData, ref object headers)
        {
            try
            {
                AxIWebBrowser2.Navigate2(ref URL, ref flags, ref targetFrameName, ref postData, ref headers);
            }
            catch (COMException ce)
            {
                if ((uint)unchecked(ce.ErrorCode) != (uint)unchecked(0x800704c7))
                {
                    // "the operation was canceled by the user" - navigation failed
                    // ignore this error, IE has already alerted the user.
                    throw;
                }
            }
        }

        private bool ShouldSerializeDocumentText()
        {
            return IsValidUrl;
        }

        private bool IsValidUrl
        {
            get
            {
                return Url is null || Url.AbsoluteUri == "about:blank";
            }
        }

        private bool ShouldSerializeUrl()
        {
            return !ShouldSerializeDocumentText();
        }

        /// <summary>
        ///  Returns true if there is a context menu to show.
        /// </summary>
        private bool ShowContextMenu(Point location)
        {
            ContextMenuStrip contextMenuStrip = ContextMenuStrip;
            if (contextMenuStrip is not null)
            {
                Point client;

                bool keyboardActivated = false;

                // X will be -1 when the user invokes the context menu from the keyboard
                if (location.X == -1)
                {
                    keyboardActivated = true;
                    client = new Point(Width / 2, Height / 2);
                }
                else
                {
                    client = PointToClient(location);
                }

                if (ClientRectangle.Contains(client))
                {
                    contextMenuStrip?.ShowInternal(this, client, keyboardActivated);

                    return true;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.MsgInternal)
            {
                case User32.WM.CONTEXTMENU:
                    if (!ShowContextMenu(PARAM.ToPoint(m.LParamInternal)))
                    {
                        DefWndProc(ref m);
                    }

                    break;
                default:
                    base.WndProc(ref m);
                    break;
            }
        }

        private Mshtml.IWebBrowser2 AxIWebBrowser2
        {
            get
            {
                if (axIWebBrowser2 is null)
                {
                    ObjectDisposedException.ThrowIf(IsDisposed, this);

                    // This should call AttachInterfaces
                    TransitionUpTo(WebBrowserHelper.AXState.InPlaceActive);
                }

                // We still don't have this.axIWebBrowser2. Throw an exception.
                return axIWebBrowser2 ?? throw new InvalidOperationException(SR.WebBrowserNoCastToIWebBrowser2);
            }
        }
    }
}
