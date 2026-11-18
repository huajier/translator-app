# Tesseract 5: geometric noise rejection (first-hand source)
src/textord/textord.cpp (params):
  textord_noise_area_ratio 0.7  "Fraction of bounding box for noise"
  textord_noise_sizefraction 10 "Fraction of size for maxima"
  textord_noise_sizelimit 0.5   "Fraction of x for big t count"
  textord_noise_translimit 16   "Transitions for normal blob"
  textord_noise_normratio 2.0   "Dot to norm ratio for deletion"
  textord_noise_rejwords true  "Reject noise-like words"
  textord_noise_rejrows  true  "Reject noise-like rows"
  textord_noise_syfract 0.2 / textord_noise_sxfract 0.4  "xh fract height/width error for norm blobs"
  textord_noise_hfract 1/64
src/textord/tordmain.cpp Textord::clean_noise_from_row():
  bool rejected = dot_count > norm_count * textord_noise_normratio && dot_count > 2;
  return super_norm_count < textord_noise_sncount && rejected;
src/textord/tordmain.cpp Textord::clean_noise_from_words():
  "if (word_dud[word_index] == 2 || (word_dud[word_index] == 1 && dud_words > ok_words))"
  -> CleanNoise(textord_noise_sizelimit * row->x_height())
=> a row made mostly of tiny/dot blobs is rejected; a word is rejected when its ratio
   dotted/normal blobs is high, AND more than half the row's words are duds.
URLs:
 https://github.com/tesseract-ocr/tesseract/blob/main/src/textord/textord.cpp
 https://github.com/tesseract-ocr/tesseract/blob/main/src/textord/tordmain.cpp
