/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>
using System;

namespace CoreJ2K.Icc.Types
{

    /// <summary> Date Time format for tags
    /// 
    /// </summary>
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public class ICCDateTime
    {
        public static readonly int size;

        /// <summary>Year datum.   </summary>
        public short wYear;
        /// <summary>Month datum.  </summary>
        // Number of the actual year (i.e. 1994)
        public short wMonth;
        /// <summary>Day datum.    </summary>
        // Number of the month (1-12)
        public short wDay;
        /// <summary>Hour datum.   </summary>
        // Number of the day
        public short wHours;
        /// <summary>Minute datum. </summary>
        // Number of hours (0-23)
        public short wMinutes;
        /// <summary>Second datum. </summary>
        // Number of minutes (0-59)
        public short wSeconds; // Number of seconds (0-59)

        /// <summary>Construct an ICCDateTime from parts </summary>
        public ICCDateTime(short year, short month, short day, short hour, short minute, short second)
        {
            wYear = year; wMonth = month; wDay = day;
            wHours = hour; wMinutes = minute; wSeconds = second;
        }

        /// <summary>Write an ICCDateTime to a file. </summary>
        public virtual void write(System.IO.Stream raf)
        {
            System.IO.BinaryWriter temp_BinaryWriter;
            temp_BinaryWriter = new System.IO.BinaryWriter(raf);
            temp_BinaryWriter.Write(wYear);
            System.IO.BinaryWriter temp_BinaryWriter2;
            temp_BinaryWriter2 = new System.IO.BinaryWriter(raf);
            temp_BinaryWriter2.Write(wMonth);
            System.IO.BinaryWriter temp_BinaryWriter3;
            temp_BinaryWriter3 = new System.IO.BinaryWriter(raf);
            temp_BinaryWriter3.Write(wDay);
            System.IO.BinaryWriter temp_BinaryWriter4;
            temp_BinaryWriter4 = new System.IO.BinaryWriter(raf);
            temp_BinaryWriter4.Write(wHours);
            System.IO.BinaryWriter temp_BinaryWriter5;
            temp_BinaryWriter5 = new System.IO.BinaryWriter(raf);
            temp_BinaryWriter5.Write(wMinutes);
            System.IO.BinaryWriter temp_BinaryWriter6;
            temp_BinaryWriter6 = new System.IO.BinaryWriter(raf);
            temp_BinaryWriter6.Write(wSeconds);
        }

        /// <summary>Return a ICCDateTime representation. </summary>
        public override string ToString()
        {
            //System.String rep = "";
            return
                $"{Convert.ToString(wYear)}/{Convert.ToString(wMonth)}/{Convert.ToString(wDay)} {Convert.ToString(wHours)}:{Convert.ToString(wMinutes)}:{Convert.ToString(wSeconds)}";
        }
        static ICCDateTime()
        {
            size = 6 * ICCProfile.short_size;
        }
    }
}