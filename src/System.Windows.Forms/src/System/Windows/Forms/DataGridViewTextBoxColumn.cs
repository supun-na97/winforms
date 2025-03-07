﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.ComponentModel;
using System.Drawing;

namespace System.Windows.Forms
{
    [ToolboxBitmap(typeof(DataGridViewTextBoxColumn), "DataGridViewTextBoxColumn")]
    public class DataGridViewTextBoxColumn : DataGridViewColumn
    {
        private const int ColumnMaxInputLength = 32767;

        public DataGridViewTextBoxColumn() : base(new DataGridViewTextBoxCell())
        {
            SortMode = DataGridViewColumnSortMode.Automatic;
        }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public override DataGridViewCell CellTemplate
        {
            get => base.CellTemplate;
            set
            {
                if (value is not null and not DataGridViewTextBoxCell)
                {
                    throw new InvalidCastException(string.Format(SR.DataGridViewTypeColumn_WrongCellTemplateType, "System.Windows.Forms.DataGridViewTextBoxCell"));
                }

                base.CellTemplate = value;
            }
        }

        [DefaultValue(ColumnMaxInputLength)]
        [SRCategory(nameof(SR.CatBehavior))]
        [SRDescription(nameof(SR.DataGridView_TextBoxColumnMaxInputLengthDescr))]
        public int MaxInputLength
        {
            get
            {
                if (TextBoxCellTemplate is null)
                {
                    throw new InvalidOperationException(SR.DataGridViewColumn_CellTemplateRequired);
                }

                return TextBoxCellTemplate.MaxInputLength;
            }
            set
            {
                if (MaxInputLength != value)
                {
                    TextBoxCellTemplate.MaxInputLength = value;
                    if (DataGridView is not null)
                    {
                        DataGridViewRowCollection dataGridViewRows = DataGridView.Rows;
                        int rowCount = dataGridViewRows.Count;
                        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
                        {
                            DataGridViewRow dataGridViewRow = dataGridViewRows.SharedRow(rowIndex);
                            if (dataGridViewRow.Cells[Index] is DataGridViewTextBoxCell dataGridViewCell)
                            {
                                dataGridViewCell.MaxInputLength = value;
                            }
                        }
                    }
                }
            }
        }

        [DefaultValue(DataGridViewColumnSortMode.Automatic)]
        public new DataGridViewColumnSortMode SortMode
        {
            get => base.SortMode;
            set => base.SortMode = value;
        }

        private DataGridViewTextBoxCell TextBoxCellTemplate => (DataGridViewTextBoxCell)CellTemplate;

        public override string ToString()
        {
            return $"DataGridViewTextBoxColumn {{ Name={Name}, Index={Index} }}";
        }
    }
}
