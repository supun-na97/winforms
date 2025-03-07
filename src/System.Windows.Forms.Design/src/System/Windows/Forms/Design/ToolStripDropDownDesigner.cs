﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Configuration;
using System.Drawing;
using System.Windows.Forms.Design.Behavior;

namespace System.Windows.Forms.Design
{
    /// <summary>
    ///  Designer for ToolStripDropDowns...just provides the Edit... verb.
    /// </summary>
    internal class ToolStripDropDownDesigner : ComponentDesigner
    {
        private ISelectionService _selectionService;
        private MenuStrip designMenu;
        private ToolStripMenuItem menuItem;
        private IDesignerHost host;
        private ToolStripDropDown dropDown;
        private bool selected;
        private ControlBodyGlyph dummyToolStripGlyph;
        private uint _editingCollection; // non-zero if the collection editor is up for this ToolStrip or a child of it.
        private FormDocumentDesigner parentFormDesigner;
        internal ToolStripMenuItem currentParent;
        private INestedContainer _nestedContainer; //NestedContainer for our DesignTime MenuItem.
        private UndoEngine _undoEngine;

        /// <summary>
        ///  ShadowProperty.
        /// </summary>
        private bool AutoClose
        {
            get => (bool)ShadowProperties[nameof(AutoClose)];
            set => ShadowProperties[nameof(AutoClose)] = value;
        }

        private bool AllowDrop
        {
            get => (bool)ShadowProperties[nameof(AllowDrop)];
            set => ShadowProperties[nameof(AllowDrop)] = value;
        }

        /// <summary>
        ///  Adds designer actions to the ActionLists collection.
        /// </summary>
        public override DesignerActionListCollection ActionLists
        {
            get
            {
                DesignerActionListCollection actionLists = new DesignerActionListCollection();
                actionLists.AddRange(base.ActionLists);
                ContextMenuStripActionList cmActionList = new ContextMenuStripActionList(this);
                if (cmActionList is not null)
                {
                    actionLists.Add(cmActionList);
                }

                // finally add the verbs for this component there...
                DesignerVerbCollection cmVerbs = Verbs;
                if (cmVerbs is not null && cmVerbs.Count != 0)
                {
                    DesignerVerb[] cmverbsArray = new DesignerVerb[cmVerbs.Count];
                    cmVerbs.CopyTo(cmverbsArray, 0);
                    actionLists.Add(new DesignerActionVerbList(cmverbsArray));
                }

                return actionLists;
            }
        }

        /// <summary>
        ///  The ToolStripItems are the associated components.   We want those to come with in any cut, copy opreations.
        /// </summary>
        public override ICollection AssociatedComponents
        {
            get => ((ToolStrip)Component).Items;
        }

        // Dummy menuItem that is used for the contextMenuStrip design
        public ToolStripMenuItem DesignerMenuItem
        {
            get => menuItem;
        }

        /// <summary>
        ///  Set by the ToolStripItemCollectionEditor when it's launched for this The Items property doesnt open another instance
        ///  of collectioneditor.  We count this so that we can deal with nestings.
        /// </summary>
        internal bool EditingCollection
        {
            get => _editingCollection != 0;
            set
            {
                if (value)
                {
                    _editingCollection++;
                }
                else
                {
                    _editingCollection--;
                }
            }
        }

        // ContextMenuStrip if Inherited ACT as Readonly.
        protected override InheritanceAttribute InheritanceAttribute
        {
            get
            {
                if ((base.InheritanceAttribute == InheritanceAttribute.Inherited))
                {
                    return InheritanceAttribute.InheritedReadOnly;
                }

                return base.InheritanceAttribute;
            }
        }

        /// <summary>
        ///  Prefilter this property so that we can set the right To Left on the Design Menu...
        /// </summary>
        private RightToLeft RightToLeft
        {
            get => dropDown.RightToLeft;
            set
            {
                if (menuItem is not null && designMenu is not null && value != RightToLeft)
                {
                    Rectangle bounds = Rectangle.Empty;
                    try
                    {
                        bounds = dropDown.Bounds;
                        menuItem.HideDropDown();
                        designMenu.RightToLeft = value;
                        dropDown.RightToLeft = value;
                    }
                    finally
                    {
                        if (bounds != Rectangle.Empty)
                        {
                            GetService<BehaviorService>()?.Invalidate(bounds);
                        }

                        ToolStripMenuItemDesigner itemDesigner = (ToolStripMenuItemDesigner)host.GetDesigner(menuItem);
                        itemDesigner?.InitializeDropDown();
                    }
                }
            }
        }

        /// <summary>
        ///  shadowing the SettingsKey so we can default it to be RootComponent.Name + "." + Control.Name
        /// </summary>
        private string SettingsKey
        {
            get
            {
                if (string.IsNullOrEmpty((string)ShadowProperties[SettingsKeyName]))
                {
                    if (Component is IPersistComponentSettings persistableComponent && host is not null)
                    {
                        if (persistableComponent.SettingsKey is null)
                        {
                            IComponent rootComponent = host.RootComponent;
                            if (rootComponent is not null && rootComponent != persistableComponent)
                            {
                                ShadowProperties[SettingsKeyName] = $"{rootComponent.Site.Name}.{Component.Site.Name}";
                            }
                            else
                            {
                                ShadowProperties[SettingsKeyName] = Component.Site.Name;
                            }
                        }

                        persistableComponent.SettingsKey = ShadowProperties[SettingsKeyName] as string;
                        return persistableComponent.SettingsKey;
                    }
                }

                return ShadowProperties[SettingsKeyName] as string;
            }
            set
            {
                ShadowProperties[SettingsKeyName] = value;
                if (Component is IPersistComponentSettings persistableComponent)
                {
                    persistableComponent.SettingsKey = value;
                }
            }
        }

        // We have to add the glyphs ourselves.
        private void AddSelectionGlyphs(SelectionManager selectionManager, ISelectionService selectionService)
        {
            //If one or many of our items are selected then Add Selection Glyphs ourselves since this is a ComponentDesigner which won't get called on the "GetGlyphs"
            ICollection selComponents = selectionService.GetSelectedComponents();
            GlyphCollection glyphs = new GlyphCollection();
            foreach (object selComp in selComponents)
            {
                if (selComp is ToolStripItem item)
                {
                    ToolStripItemDesigner itemDesigner = (ToolStripItemDesigner)host.GetDesigner(item);
                    itemDesigner?.GetGlyphs(ref glyphs, new ResizeBehavior(item.Site));
                }
            }

            // Get the Glyphs union Rectangle.
            if (glyphs.Count > 0)
            {
                // Add Glyphs and then invalidate the unionRect
                selectionManager.SelectionGlyphAdorner.Glyphs.AddRange(glyphs);
            }
        }

        // internal method called by outside designers to add glyphs for the ContextMenuStrip
        internal void AddSelectionGlyphs()
        {
            if (TryGetService(out SelectionManager selectionManager))
            {
                AddSelectionGlyphs(selectionManager, _selectionService);
            }
        }

        /// <summary>
        ///  Disposes of this designer.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Unhook our services
                if (_selectionService is not null)
                {
                    _selectionService.SelectionChanged -= new EventHandler(OnSelectionChanged);
                    _selectionService.SelectionChanging -= new EventHandler(OnSelectionChanging);
                }

                DisposeMenu();
                if (designMenu is not null)
                {
                    designMenu.Dispose();
                    designMenu = null;
                }

                if (dummyToolStripGlyph is not null)
                {
                    dummyToolStripGlyph = null;
                }

                if (_undoEngine is not null)
                {
                    _undoEngine.Undone -= new EventHandler(OnUndone);
                }
            }

            base.Dispose(disposing);
        }

        /// <summary>
        ///  Disposes of this dummy menuItem and its designer..
        /// </summary>
        private void DisposeMenu()
        {
            HideMenu();
            if (host.RootComponent is Control form)
            {
                if (designMenu is not null)
                {
                    form.Controls.Remove(designMenu);
                }

                if (menuItem is not null)
                {
                    if (_nestedContainer is not null)
                    {
                        _nestedContainer.Dispose();
                        _nestedContainer = null;
                    }

                    menuItem.Dispose();
                    menuItem = null;
                }
            }
        }

        // private helper function to Hide the ContextMenu structure.
        private void HideMenu()
        {
            if (menuItem is null)
            {
                return;
            }

            selected = false;

            if (!(host.RootComponent is Control))
            {
                return;
            }

            menuItem.DropDown.AutoClose = true;
            menuItem.HideDropDown();
            menuItem.Visible = false;

            // Hide the MenuItem DropDown.
            designMenu.Visible = false;

            // Invalidate the Bounds..
            // toolStripAdornerWindowService.Invalidate(boundsToInvalidate);
            GetService<ToolStripAdornerWindowService>()?.Invalidate();

            // Query for the Behavior Service and Remove Glyph....
            if (TryGetService(out BehaviorService _))
            {
                if (dummyToolStripGlyph is not null && TryGetService(out SelectionManager selectionManager))
                {
                    if (selectionManager.BodyGlyphAdorner.Glyphs.Contains(dummyToolStripGlyph))
                    {
                        selectionManager.BodyGlyphAdorner.Glyphs.Remove(dummyToolStripGlyph);
                    }

                    selectionManager.Refresh();
                }

                dummyToolStripGlyph = null;
            }

            // Unhook all the events for DesignMenuItem
            if (menuItem is not null)
            {
                if (host.GetDesigner(menuItem) is ToolStripMenuItemDesigner itemDesigner)
                {
                    itemDesigner.UnHookEvents();
                    itemDesigner.RemoveTypeHereNode(menuItem);
                }
            }
        }

        /// <summary>
        ///  Initialize the item.
        /// </summary>
        public override void Initialize(IComponent component)
        {
            base.Initialize(component);
            host = (IDesignerHost)GetService(typeof(IDesignerHost));

            // Add the EditService so that the ToolStrip can do its own Tab and Keyboard Handling
            ToolStripKeyboardHandlingService keyboardHandlingService = (ToolStripKeyboardHandlingService)GetService(typeof(ToolStripKeyboardHandlingService));
            keyboardHandlingService ??= new ToolStripKeyboardHandlingService(component.Site);

            // Add the InsituEditService so that the ToolStrip can do its own Insitu Editing
            ISupportInSituService inSituService = (ISupportInSituService)GetService(typeof(ISupportInSituService));
            inSituService ??= new ToolStripInSituService(Component.Site);

            dropDown = (ToolStripDropDown)Component;
            dropDown.Visible = false;

            // Shadow properties as we would change these for DropDowns at DesignTime.
            AutoClose = dropDown.AutoClose;
            AllowDrop = dropDown.AllowDrop;

            if (TryGetService(out _selectionService))
            {
                // first select the rootComponent and then hook on the events... but not if we are loading - VSWhidbey #484576
                if (host is not null && !host.Loading)
                {
                    _selectionService.SetSelectedComponents(new IComponent[] { host.RootComponent }, SelectionTypes.Replace);
                }

                _selectionService.SelectionChanging += new EventHandler(OnSelectionChanging);
                _selectionService.SelectionChanged += new EventHandler(OnSelectionChanged);
            }

            designMenu = new MenuStrip
            {
                Visible = false,
                AutoSize = false,
                Dock = DockStyle.Top
            };
            if (DpiHelper.IsScalingRequired)
            {
                designMenu.Height = DpiHelper.LogicalToDeviceUnitsY(designMenu.Height);
            }

            // Add MenuItem
            if (host.RootComponent is Control form)
            {
                menuItem = new ToolStripMenuItem
                {
                    BackColor = SystemColors.Window,
                    Name = Component.Site.Name
                };
                menuItem.Text = (dropDown is not null) ? dropDown.GetType().Name : menuItem.Name;
                designMenu.Items.Add(menuItem);
                form.Controls.Add(designMenu);
                designMenu.SendToBack();

                if (TryGetService(out _nestedContainer))
                {
                    _nestedContainer.Add(menuItem, "ContextMenuStrip");
                }
            }

            // EditorServiceContext is newed up to add Edit Items verb.
            new EditorServiceContext(this, TypeDescriptor.GetProperties(Component)["Items"], SR.ToolStripItemCollectionEditorVerb);

            // Use the UndoEngine.Undone to show the dropdown again
            if (_undoEngine is null && TryGetService(out _undoEngine))
            {
                _undoEngine.Undone += new EventHandler(OnUndone);
            }
        }

        // Helper function to check if the ToolStripItem on the ContextMenu is selected.
        private bool IsContextMenuStripItemSelected(ISelectionService selectionService)
        {
            bool showDesignMenu = false;
            if (menuItem is null)
            {
                return showDesignMenu;
            }

            ToolStripDropDown topmost = null;
            IComponent comp = (IComponent)selectionService.PrimarySelection;
            if (comp is null && dropDown.Visible)
            {
                if (TryGetService(out ToolStripKeyboardHandlingService keyboardHandlingService))
                {
                    comp = (IComponent)keyboardHandlingService.SelectedDesignerControl;
                }
            }

            // This case covers (a) and (b) above....
            if (comp is ToolStripDropDownItem currentItem)
            {
                if (currentItem == menuItem)
                {
                    topmost = menuItem.DropDown;
                }
                else
                {
                    ToolStripMenuItemDesigner itemDesigner = (ToolStripMenuItemDesigner)host.GetDesigner(comp);
                    if (itemDesigner is not null)
                    {
                        topmost = ToolStripItemDesigner.GetFirstDropDown(currentItem);
                    }
                }
            }
            else if (comp is ToolStripItem) //case (c)
            {
                if (!(((ToolStripItem)comp).GetCurrentParent() is ToolStripDropDown parent))
                {
                    // Try if the item has not laid out...
                    parent = ((ToolStripItem)comp).Owner as ToolStripDropDown;
                }

                if (parent is not null && parent.Visible)
                {
                    ToolStripItem ownerItem = parent.OwnerItem;
                    if (ownerItem is not null && ownerItem == menuItem)
                    {
                        topmost = menuItem.DropDown;
                    }
                    else
                    {
                        ToolStripMenuItemDesigner itemDesigner = (ToolStripMenuItemDesigner)host.GetDesigner(ownerItem);
                        if (itemDesigner is not null)
                        {
                            topmost = ToolStripItemDesigner.GetFirstDropDown((ToolStripDropDownItem)ownerItem);
                        }
                    }
                }
            }

            if (topmost is not null)
            {
                ToolStripItem topMostItem = topmost.OwnerItem;
                if (topMostItem == menuItem)
                {
                    showDesignMenu = true;
                }
            }

            return showDesignMenu;
        }

        /// <summary>
        ///  Listens SelectionChanging to Show the MenuDesigner.
        /// </summary>
        private void OnSelectionChanging(object sender, EventArgs e)
        {
            ISelectionService selectionService = (ISelectionService)sender;
            // If we are no longer selected ... Hide the DropDown
            bool showDesignMenu = IsContextMenuStripItemSelected(selectionService) || Component.Equals(selectionService.PrimarySelection);
            if (selected && !showDesignMenu)
            {
                HideMenu();
            }
        }

        /// <summary>
        ///  Listens SelectionChanged to Show the MenuDesigner.
        /// </summary>
        private void OnSelectionChanged(object sender, EventArgs e)
        {
            if (Component is null || menuItem is null)
            {
                return;
            }

            ISelectionService selectionService = (ISelectionService)sender;
            // Select the container if TopLevel Dummy MenuItem is selected.
            if (selectionService.GetComponentSelected(menuItem))
            {
                selectionService.SetSelectedComponents(new IComponent[] { Component }, SelectionTypes.Replace);
            }

            // return if DropDown is already is selected.
            if (Component.Equals(selectionService.PrimarySelection) && selected)
            {
                return;
            }

            bool showDesignMenu = IsContextMenuStripItemSelected(selectionService) || Component.Equals(selectionService.PrimarySelection);

            if (showDesignMenu)
            {
                if (!dropDown.Visible)
                {
                    ShowMenu();
                }

                // Selection change would remove our Glyph from the BodyGlyph Collection.
                if (TryGetService(out SelectionManager selectionManager))
                {
                    if (dummyToolStripGlyph is not null)
                    {
                        selectionManager.BodyGlyphAdorner.Glyphs.Insert(0, dummyToolStripGlyph);
                    }

                    // Add our SelectionGlyphs and Invalidate.
                    AddSelectionGlyphs(selectionManager, selectionService);
                }
            }
        }

        /// <summary>
        ///  Allows a designer to filter the set of properties the component it is designing will expose through the TypeDescriptor object.  This method is called immediately before its corresponding "Post" method. If you are overriding this method you should call the base implementation before you perform your own filtering.
        /// </summary>
        protected override void PreFilterProperties(IDictionary properties)
        {
            base.PreFilterProperties(properties);
            PropertyDescriptor prop;
            string[] shadowProps = new string[] { "AutoClose", SettingsKeyName, "RightToLeft", "AllowDrop" };
            Attribute[] empty = Array.Empty<Attribute>();
            for (int i = 0; i < shadowProps.Length; i++)
            {
                prop = (PropertyDescriptor)properties[shadowProps[i]];
                if (prop is not null)
                {
                    properties[shadowProps[i]] = TypeDescriptor.CreateProperty(typeof(ToolStripDropDownDesigner), prop, empty);
                }
            }
        }

        // Reset Settings.
        public void ResetSettingsKey()
        {
            if (Component is IPersistComponentSettings)
            {
                SettingsKey = null;
            }
        }

        /// <summary>
        /// Resets the ToolStripDropDown AutoClose to be the default padding
        /// </summary>
        private void ResetAutoClose()
        {
            ShadowProperties[nameof(AutoClose)] = true;
        }

        /// <summary>
        /// Restores the ToolStripDropDown AutoClose to be the value set in the property grid.
        /// </summary>
        private void RestoreAutoClose()
        {
            dropDown.AutoClose = (bool)ShadowProperties[nameof(AutoClose)];
        }

        /// <summary>
        /// Resets the ToolStripDropDown AllowDrop to be the default padding
        /// </summary>
        private void ResetAllowDrop()
        {
            ShadowProperties[nameof(AllowDrop)] = false;
        }

        /// <summary>
        /// Restores the ToolStripDropDown AllowDrop to be the value set in the property grid.
        /// </summary>
        private void RestoreAllowDrop()
        {
            dropDown.AutoClose = (bool)ShadowProperties[nameof(AllowDrop)];
        }

        /// <summary>
        /// Resets the ToolStripDropDown RightToLeft to be the default RightToLeft
        /// </summary>
        private void ResetRightToLeft()
        {
            RightToLeft = RightToLeft.No;
        }

        /// <summary>
        ///  Show the MenuDesigner; used by ToolStripMenuItemDesigner to show the menu when the user selects the dropDown item through the PG or Document outline. The editor node will be selected by default.
        /// </summary>
        public void ShowMenu()
        {
            int count = dropDown.Items.Count - 1;
            if (count >= 0)
            {
                ShowMenu(dropDown.Items[count]);
            }
            else
            {
                ShowMenu(null);
            }
        }

        /// <summary>
        ///  Show the MenuDesigner; used by ToolStripMenuItemDesigner to show the menu when the user selects the dropDown item through the PG or Document outline. The input toolstrip item will be selected.
        /// </summary>
        public void ShowMenu(ToolStripItem selectedItem)
        {
            if (menuItem is null)
            {
                return;
            }

            Control parent = designMenu.Parent;
            if (parent is Form parentForm)
            {
                parentFormDesigner = host.GetDesigner(parentForm) as FormDocumentDesigner;
            }

            selected = true;
            designMenu.Visible = true;
            designMenu.BringToFront();
            menuItem.Visible = true;

            // Check if this is a design-time DropDown
            if (currentParent is not null && currentParent != menuItem)
            {
                if (host.GetDesigner(currentParent) is ToolStripMenuItemDesigner ownerItemDesigner)
                {
                    ownerItemDesigner.RemoveTypeHereNode(currentParent);
                }
            }

            // Every time you hide/show .. set the DropDown of the designer MenuItem to the component dropDown being designed.
            menuItem.DropDown = dropDown;
            menuItem.DropDown.OwnerItem = menuItem;
            if (dropDown.Items.Count > 0)
            {
                ToolStripItem[] items = new ToolStripItem[dropDown.Items.Count];
                dropDown.Items.CopyTo(items, 0);
                foreach (ToolStripItem toolItem in items)
                {
                    if (toolItem is DesignerToolStripControlHost)
                    {
                        dropDown.Items.Remove(toolItem);
                    }
                }
            }

            ToolStripMenuItemDesigner itemDesigner = (ToolStripMenuItemDesigner)host.GetDesigner(menuItem);
            if (TryGetService(out BehaviorService behaviorService))
            {
                // Show the contextMenu only if the dummy menuStrip is contained in the Form. Refer to VsWhidbey 484317 for more details.
                if (itemDesigner is not null && parent is not null)
                {
                    Rectangle parentBounds = behaviorService.ControlRectInAdornerWindow(parent);
                    Rectangle menuBounds = behaviorService.ControlRectInAdornerWindow(designMenu);
                    if (ToolStripDesigner.IsGlyphTotallyVisible(menuBounds, parentBounds))
                    {
                        itemDesigner.InitializeDropDown();
                    }
                }

                if (dummyToolStripGlyph is null)
                {
                    Point loc = behaviorService.ControlToAdornerWindow(designMenu);
                    Rectangle r = designMenu.Bounds;
                    r.Offset(loc);
                    dummyToolStripGlyph = new ControlBodyGlyph(r, Cursor.Current, menuItem, new ContextMenuStripBehavior(menuItem));
                    GetService<SelectionManager>()?.BodyGlyphAdorner.Glyphs.Insert(0, dummyToolStripGlyph);
                }

                if (selectedItem is not null)
                {
                    GetService<ToolStripKeyboardHandlingService>().SelectedDesignerControl = selectedItem;
                }
            }
        }

        // Should the designer serialize the settings?
        private bool ShouldSerializeSettingsKey() => (Component is IPersistComponentSettings persistableComponent && persistableComponent.SaveSettings && SettingsKey is not null);

        /// <summary>
        /// Since we're shadowing ToolStripDropDown AutoClose, we get called here to determine whether or not to serialize
        /// </summary>
        private bool ShouldSerializeAutoClose() => (!(bool)ShadowProperties[nameof(AutoClose)]);

        /// <summary>
        /// Since we're shadowing ToolStripDropDown AllowDrop, we get called here to determine whether or not to serialize
        /// </summary>
        private bool ShouldSerializeAllowDrop() => AllowDrop;

        /// <summary>
        /// Since we're shadowing ToolStripDropDown RightToLeft, we get called here to determine whether or not to serialize
        /// </summary>
        private bool ShouldSerializeRightToLeft() => RightToLeft != RightToLeft.No;

        /// <summary>
        ///  ResumeLayout after Undone.
        /// </summary>
        private void OnUndone(object source, EventArgs e)
        {
            if (_selectionService is not null && Component.Equals(_selectionService.PrimarySelection))
            {
                HideMenu();
                ShowMenu();
            }
        }

        /// <summary>
        ///  This is an internal class which provides the Behavior for our MenuStrip Body Glyph. This will just eat the MouseUps...
        /// </summary>
        internal class ContextMenuStripBehavior : Behavior.Behavior
        {
            private readonly ToolStripMenuItem _item;
            internal ContextMenuStripBehavior(ToolStripMenuItem menuItem)
            {
                _item = menuItem;
            }

            public override bool OnMouseUp(Glyph g, MouseButtons button)
            {
                if (button == MouseButtons.Left)
                {
                    return true;
                }

                return false;
            }
        }
    }
}
