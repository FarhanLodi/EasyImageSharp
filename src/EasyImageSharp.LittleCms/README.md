# EasyImageSharp.LittleCms (moved)

The Little CMS ICC transforms now live in **[EasyImageSharp.Codecs](https://www.nuget.org/packages/EasyImageSharp.Codecs)**,
together with the AVIF and JPEG XL codecs. The namespace (`EasyImageSharp.LittleCms`) and the API are unchanged.

This package contains no code of its own any more: it depends on EasyImageSharp.Codecs and forwards every
`EasyImageSharp.LittleCms` type to it, so existing projects keep compiling and applications built against 1.2.0
keep running. To migrate, replace the package reference:

```
dotnet remove package EasyImageSharp.LittleCms
dotnet add package EasyImageSharp.Codecs
```

Documentation, native runtime deployment and licence notices are in the EasyImageSharp.Codecs package.
