﻿#region Header

// Copyright (C) Jan Tolenaar. See the file LICENSE for details.

#endregion Header

namespace Kiezel
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    public class ReplHistory
    {
        #region Fields

        private int cursor;
        private string histfile;
        private List<string> lines;

        #endregion Fields

        #region Constructors

        public ReplHistory()
        {
            cursor = 0;
            lines = new List<string>();
            histfile = null;

            var dir = Runtime.CreateKiezellispDataFolder();

            histfile = PathExtensions.Combine(dir, "kiezellisp.history");

            if (File.Exists(histfile))
            {
                foreach (var ln in File.ReadAllLines(histfile))
                {
                    lines.Add(ln.Replace("<CRLF>", "\n"));
                }
                cursor = lines.Count;
            }
        }

        #endregion Constructors

        #region Public Properties

        public int Count
        {
            get
            {
                return lines.Count;
            }
        }

        #endregion Public Properties

        #region Public Methods

        public void Append(string s)
        {
            s = s.TrimEnd();

            switch (s)
            {
                case ":q":
                case ":qu":
                case ":qui":
                case ":quit":
                    {
                        break;
                    }
                default:
                    {
                        if (lines.Contains(s))
                        {
                            lines.Remove(s);
                        }
                        lines.Add(s);
                        break;
                    }
            }

            cursor = Count;
        }

        public void Clear(int keep = 0)
        {
            var k = Math.Min(lines.Count, keep);
            var n = lines.Count - k;
            lines = lines.GetRange(n, k);
            cursor = Count;
        }

        public void Save()
        {
            if (histfile == null)
            {
                return;
            }

            try
            {
                using (StreamWriter sw = File.CreateText(histfile))
                {
                    Clear(25);

                    foreach (string s in lines)
                    {
                        sw.WriteLine(s.Replace("\n", "<CRLF>"));
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        public string Line(int index)
        {
            if (0 <= index && index < lines.Count)
            {
                return lines[index];
            }
            else
            {
                return "";
            }
        }

        public string Next()
        {
            if (cursor < Count)
            {
                ++cursor;
            }

            return Line(cursor);
        }

        public string Previous()
        {
            if (cursor >= 0)
            {
                --cursor;
            }

            return Line(cursor);
        }

        #endregion Public Methods
    }
}