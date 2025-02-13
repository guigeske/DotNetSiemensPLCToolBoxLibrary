﻿/*
 This implements a high level Wrapper between libnodave.dll and applications written
 in MS .Net languages.

 This ConnectionLibrary was written by Jochen Kuehner
 * http://jfk-solutuions.de/
 *
 * Thanks go to:
 * Steffen Krayer -> For his work on MC7 decoding and the Source for his Decoder
 * Zottel         -> For LibNoDave

 WPFToolboxForSiemensPLCs is free software; you can redistribute it and/or modify
 it under the terms of the GNU Library General Public License as published by
 the Free Software Foundation; either version 2, or (at your option)
 any later version.

 WPFToolboxForSiemensPLCs is distributed in the hope that it will be useful,
 but WITHOUT ANY WARRANTY; without even the implied warranty of
 MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 GNU General Public License for more details.

 You should have received a copy of the GNU Library General Public License
 along with Libnodave; see the file COPYING.  If not, write to
 the Free Software Foundation, 675 Mass Ave, Cambridge, MA 02139, USA.
*/

using System;

namespace DotNetSiemensPLCToolBoxLibrary.DataTypes.Blocks.Step7V11
{
    [Serializable()]
    public class TIAFunctionBlock : TIAProgrammBlock
    {
        //        public TIAFunctionBlock(Step7ProjectV11 TIAProject, XmlNode Node)
        //            : base(TIAProject, Node)
        //        { }

        //        public IDataRow Structure
        //        {
        //            get
        //            {
        //                return new TIADataRow(node, tiaProject, this);
        //            }
        //            set
        //            {
        //            }
        //        }

        //        public override string ToString()
        //        {
        //            var ret = "Block:" + this.Name + " (" + this.BlockName + ")" + Environment.NewLine;
        //            ret += "Titel:" + (this.Title ?? "") + Environment.NewLine;
        //            ret += "Comment:" + (this.Comment ?? "") + Environment.NewLine;
        //            return ret;
        //        }
    }
}