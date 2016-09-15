﻿using System;

namespace NotLiteCode___Server
{
    public class SharedClass
        : Prototypes // Not required, but good for debugging's sake
    {
        [NLCCall] // Any method without the [NLCCall] attribute will not be executable by the client
        public string CombineTwoStringsAndReturn(string s1, string s2)
        {
            return "Magical server says, s1 + s2 = " + s1 + s2;
        }

        [NLCCall]
        public void Test()
        {
            Console.Write("Hey! The client invoked me!");
        }

        [NLCCall]
        public void SpeedTest()
        { }
    }
}